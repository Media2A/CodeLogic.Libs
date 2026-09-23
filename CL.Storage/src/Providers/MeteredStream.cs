using System.Diagnostics;
using CL.Storage.Models;

namespace CL.Storage.Providers;

/// <summary>
/// Shares a byte-per-second budget between every stream that draws from it, so concurrent transfers on
/// one connection (or across the library) stay under the limit together, like FileZilla's speed limit.
/// </summary>
internal sealed class TokenBucket
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly double _rate;
    private double _tokens;
    private long _stamp;

    public TokenBucket(long bytesPerSecond, TimeProvider? time = null)
    {
        if (bytesPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));
        _time = time ?? TimeProvider.System;
        _rate = bytesPerSecond;
        // A burst of a quarter second keeps short transfers responsive without exceeding the average.
        _tokens = bytesPerSecond / 4.0;
        _stamp = _time.GetTimestamp();
    }

    public long BytesPerSecond => (long)_rate;

    /// <summary>Waits until <paramref name="bytes"/> may pass. Debt is allowed, so large reads are never starved.</summary>
    public async ValueTask TakeAsync(int bytes, CancellationToken cancellationToken)
    {
        TimeSpan wait;
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            _tokens = Math.Min(_rate / 4.0, _tokens + _time.GetElapsedTime(_stamp, now).TotalSeconds * _rate);
            _stamp = now;
            _tokens -= bytes;
            wait = _tokens >= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(-_tokens / _rate);
        }
        if (wait > TimeSpan.Zero)
            await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Wraps a read stream to report progress and enforce bandwidth limits. Seeking passes through (so upload
/// retries can still rewind), and rewinding restarts the byte count from the new position.
/// </summary>
internal sealed class MeteredStream : Stream
{
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(250);
    private readonly Stream _inner;
    private readonly IProgress<StorageTransferProgress>? _progress;
    private readonly TokenBucket?[] _buckets;
    private readonly long? _total;
    private readonly string? _itemPath;
    private readonly bool _leaveOpen;
    private readonly long _origin;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _transferred;
    private TimeSpan _lastReport = -ReportInterval; // lets the first read report immediately
    private bool _completed;

    public MeteredStream(Stream inner, IProgress<StorageTransferProgress>? progress, long? total, string? itemPath, bool leaveOpen, params TokenBucket?[] buckets)
    {
        _inner = inner;
        _progress = progress;
        _total = total;
        _itemPath = itemPath;
        _leaveOpen = leaveOpen;
        _buckets = buckets.Where(bucket => bucket is not null).ToArray();
        _origin = inner.CanSeek ? inner.Position : 0;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var position = _inner.Seek(offset, origin);
        _transferred = Math.Max(0, position - _origin);
        _completed = false;
        return position;
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        foreach (var bucket in _buckets)
            await bucket!.TakeAsync(read, cancellationToken).ConfigureAwait(false);
        _transferred += read;
        Report(read == 0 || (_total is { } total && _transferred >= total));
        return read;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen) _inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_leaveOpen) await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private void Report(bool complete)
    {
        if (_progress is null || _completed) return;
        var elapsed = _clock.Elapsed;
        if (!complete && elapsed - _lastReport < ReportInterval) return;
        _lastReport = elapsed;
        _completed = complete;
        var rate = elapsed.TotalSeconds > 0 ? _transferred / elapsed.TotalSeconds : 0;
        TimeSpan? eta = _total is { } total && rate > 0 && !complete
            ? TimeSpan.FromSeconds(Math.Max(0, total - _transferred) / rate)
            : complete ? TimeSpan.Zero : null;
        _progress.Report(new StorageTransferProgress(_transferred, _total, complete, rate, eta, _itemPath));
    }
}
