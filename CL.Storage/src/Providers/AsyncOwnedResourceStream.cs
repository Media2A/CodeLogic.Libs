namespace CL.Storage.Providers;

/// <summary>Streams from a pooled resource and releases the resource when the stream is disposed.</summary>
/// <param name="stream">Inner stream read by the caller.</param>
/// <param name="releaseAsync">Returns the owning resource; invoked once on disposal.</param>
/// <param name="onFault">Invoked when a read or the inner disposal fails, so the resource is not reused.</param>
internal sealed class AsyncOwnedResourceStream(Stream stream, Func<ValueTask> releaseAsync, Action? onFault = null) : Stream
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
    public override int Read(byte[] buffer, int offset, int count)
    {
        try { return Inner.Read(buffer, offset, count); }
        catch (Exception error) when (Fault(error)) { throw; }
    }

    public override int Read(Span<byte> buffer)
    {
        try { return Inner.Read(buffer); }
        catch (Exception error) when (Fault(error)) { throw; }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try { return await Inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (Fault(error)) { throw; }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
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
            catch (Exception error) when (Fault(error)) { throw; }
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
        catch (Exception error) when (Fault(error)) { throw; }
        finally
        {
            if (release is not null) await release().ConfigureAwait(false);
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>Reports a transport failure; returns false so the exception filter never swallows it.</summary>
    /// <remarks>Any failure counts, including cancellation: a half-read transfer leaves the session in an unknown state.</remarks>
    private bool Fault(Exception error)
    {
        try { onFault?.Invoke(); }
        catch { /* Fault reporting must not replace the original exception. */ }
        return false;
    }
}
