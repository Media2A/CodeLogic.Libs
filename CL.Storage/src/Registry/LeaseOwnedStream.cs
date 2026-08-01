namespace CL.Storage.Registry;

internal sealed class LeaseOwnedStream : Stream
{
    private Stream? _inner;
    private IDisposable? _lease;

    public LeaseOwnedStream(Stream inner, IDisposable lease)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _lease = lease ?? throw new ArgumentNullException(nameof(lease));
    }

    private Stream Inner => _inner ?? throw new ObjectDisposedException(nameof(LeaseOwnedStream));

    public override bool CanRead => _inner?.CanRead ?? false;
    public override bool CanSeek => _inner?.CanSeek ?? false;
    public override bool CanWrite => _inner?.CanWrite ?? false;
    public override long Length => Inner.Length;
    public override long Position { get => Inner.Position; set => Inner.Position = value; }
    public override void Flush() => Inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => Inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => Inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => Inner.Read(buffer);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Inner.ReadAsync(buffer, cancellationToken);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Inner.ReadAsync(buffer, offset, count, cancellationToken);
    public override int ReadByte() => Inner.ReadByte();
    public override long Seek(long offset, SeekOrigin origin) => Inner.Seek(offset, origin);
    public override void SetLength(long value) => Inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => Inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => Inner.Write(buffer);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        Inner.WriteAsync(buffer, cancellationToken);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Inner.WriteAsync(buffer, offset, count, cancellationToken);
    public override void WriteByte(byte value) => Inner.WriteByte(value);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            var inner = Interlocked.Exchange(ref _inner, null);
            var lease = Interlocked.Exchange(ref _lease, null);
            try { inner?.Dispose(); }
            finally { lease?.Dispose(); }
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        var inner = Interlocked.Exchange(ref _inner, null);
        var lease = Interlocked.Exchange(ref _lease, null);
        try
        {
            if (inner is not null)
                await inner.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            lease?.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
