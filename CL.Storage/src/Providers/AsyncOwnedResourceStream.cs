namespace CL.Storage.Providers;

internal sealed class AsyncOwnedResourceStream(Stream stream, Func<ValueTask> releaseAsync) : Stream
{
    private Stream? _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    private Func<ValueTask>? _releaseAsync = releaseAsync ?? throw new ArgumentNullException(nameof(releaseAsync));
    private Stream Inner => _stream ?? throw new ObjectDisposedException(nameof(AsyncOwnedResourceStream));

    public override bool CanRead => _stream?.CanRead ?? false;
    public override bool CanSeek => _stream?.CanSeek ?? false;
    public override bool CanWrite => _stream?.CanWrite ?? false;
    public override long Length => Inner.Length;
    public override long Position { get => Inner.Position; set => Inner.Position = value; }
    public override void Flush() => Inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => Inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => Inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => Inner.Read(buffer);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => Inner.ReadAsync(buffer, cancellationToken);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Inner.ReadAsync(buffer, offset, count, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => Inner.Seek(offset, origin);
    public override void SetLength(long value) => Inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => Inner.Write(buffer, offset, count);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            var inner = Interlocked.Exchange(ref _stream, null);
            var release = Interlocked.Exchange(ref _releaseAsync, null);
            try { inner?.Dispose(); }
            finally { release?.Invoke().AsTask().GetAwaiter().GetResult(); }
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        var inner = Interlocked.Exchange(ref _stream, null);
        var release = Interlocked.Exchange(ref _releaseAsync, null);
        try
        {
            if (inner is not null) await inner.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (release is not null) await release().ConfigureAwait(false);
        }
        GC.SuppressFinalize(this);
    }
}
