namespace CL.Storage.Providers;

internal sealed class OwnedResourceStream(Stream stream, IDisposable resource) : Stream
{
    private Stream? _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    private IDisposable? _resource = resource ?? throw new ArgumentNullException(nameof(resource));
    private Stream Inner => _stream ?? throw new ObjectDisposedException(nameof(OwnedResourceStream));

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
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Inner.ReadAsync(buffer, offset, count, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => Inner.Seek(offset, origin);
    public override void SetLength(long value) => Inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => Inner.Write(buffer, offset, count);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            var inner = Interlocked.Exchange(ref _stream, null);
            var owner = Interlocked.Exchange(ref _resource, null);
            try { inner?.Dispose(); }
            finally { owner?.Dispose(); }
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        var inner = Interlocked.Exchange(ref _stream, null);
        var owner = Interlocked.Exchange(ref _resource, null);
        try
        {
            if (inner is not null)
                await inner.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            owner?.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
