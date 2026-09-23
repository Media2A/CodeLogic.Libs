using System.IO.Pipelines;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CodeLogic.Core.Results;

namespace CL.Storage.Abstractions;

/// <summary>
/// A stream the caller writes a file's content into. Nothing is visible at the destination until
/// <see cref="CommitAsync"/> succeeds; <see cref="AbortAsync"/>, or disposing without committing, discards
/// everything. Written bytes flow straight to a staging object with at most 1 MiB buffered, so a slow
/// destination slows the writer down rather than filling memory.
/// </summary>
public sealed class StorageWriteStream : Stream
{
    private readonly Pipe _pipe;
    private readonly IStorageService _destination;
    private readonly string _path;
    private readonly StorageUploadOptions _options;
    private readonly bool _overwrite;
    private readonly Task<StagedWriteResult> _upload;
    private readonly CancellationTokenSource _cancel;
    private long _written;
    private int _state; // 0 open, 1 committing or committed, 2 aborted

    internal StorageWriteStream(IStorageService destination, string path, StorageUploadOptions options, bool overwrite, CancellationToken cancellationToken)
    {
        _destination = destination;
        _path = path;
        _options = options;
        _overwrite = overwrite;
        _cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: StagedWriter.PauseWriterThreshold,
            resumeWriterThreshold: StagedWriter.ResumeWriterThreshold,
            minimumSegmentSize: StagedWriter.SegmentSize,
            useSynchronizationContext: false));
        var reader = _pipe.Reader;
        _upload = Task.Run(() => StagedWriter.WriteAsync(
            destination,
            new StagedWriteRequest
            {
                Path = path,
                Upload = options,
                ExpectedLength = options.ExpectedLength,
                Verify = options.Verify,
                ExpectedSha256 = options.ExpectedSha256
            },
            (_, _) => Task.FromResult(Result<Stream>.Success(reader.AsStream())),
            _cancel.Token));
    }

    /// <summary>Gets the destination path the content will be committed to.</summary>
    public string DestinationPath => _path;

    /// <summary>Gets the number of bytes written so far.</summary>
    public long BytesWritten => Interlocked.Read(ref _written);

    /// <inheritdoc />
    public override bool CanRead => false;
    /// <inheritdoc />
    public override bool CanSeek => false;
    /// <inheritdoc />
    public override bool CanWrite => Volatile.Read(ref _state) == 0;
    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();
    /// <inheritdoc />
    public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        if (_upload.IsCompleted)
            throw new IOException("The destination stopped accepting data.", (await _upload.ConfigureAwait(false)).Error is { } error ? new StorageWriteException(error) : null);
        var flush = await _pipe.Writer.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        Interlocked.Add(ref _written, buffer.Length);
        if (flush.IsCompleted)
            throw new IOException("The destination stopped accepting data.");
    }

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override void Flush() { }

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Finishes the content, checks its length and digest when those were requested, and moves it into place.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the commit.</param>
    /// <returns>The committed item, or why it was not committed; nothing is left at the destination on failure.</returns>
    public async Task<Result<StorageItem>> CommitAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            return Result<StorageItem>.Failure(StorageErrors.Conflict("The write was already committed or aborted."));
        await _pipe.Writer.CompleteAsync().ConfigureAwait(false);
        using var stop = cancellationToken.Register(() => _cancel.Cancel());
        // The staged write cleans up after itself when cancelled, so it is always awaited to the end.
        var written = await _upload.ConfigureAwait(false);
        if (written.Cancelled) cancellationToken.ThrowIfCancellationRequested();
        if (!written.IsSuccess)
            return Result<StorageItem>.Failure(written.Error!);
        var staging = written.Content!.StagingPath;
        Result promoted;
        try
        {
            if (_options.Condition is { IsEmpty: false } condition)
            {
                var check = await StagedWriter.CheckConditionAsync(_destination, _path, condition, cancellationToken).ConfigureAwait(false);
                if (check.IsFailure)
                {
                    await StagedWriter.DeleteAsync(_destination, staging).ConfigureAwait(false);
                    return Result<StorageItem>.Failure(check.Error!);
                }
            }
            (promoted, _) = await StagedWriter.PromoteAsync(
                _destination, staging, _path, _overwrite, _options.Condition, _options.CreateParents, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StagedWriter.DeleteAsync(_destination, staging).ConfigureAwait(false);
            throw;
        }
        if (promoted.IsFailure)
        {
            await StagedWriter.DeleteAsync(_destination, staging).ConfigureAwait(false);
            return Result<StorageItem>.Failure(promoted.Error!);
        }
        // Committed: report the result even if the caller cancels now.
        return await StagedWriter.ConfirmPromotedAsync(_destination, _path, written.Content, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Discards everything written; the destination is left as it was.</summary>
    /// <returns>A task that completes when the staging object is removed.</returns>
    public async Task AbortAsync()
    {
        if (!BeginAbort()) return;
        try { await _upload.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    /// <summary>Stops the staged write; it removes its staging object on its own.</summary>
    private bool BeginAbort()
    {
        if (Interlocked.CompareExchange(ref _state, 2, 0) != 0)
            return false;
        _pipe.Writer.Complete(new OperationCanceledException("The write was aborted."));
        _cancel.Cancel();
        return true;
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await AbortAsync().ConfigureAwait(false);
        _cancel.Dispose();
        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Disposing synchronously without committing aborts without waiting: the staging object is removed in the
    /// background. Prefer <see cref="DisposeAsync"/> or <see cref="AbortAsync"/> to know when it is gone.
    /// </remarks>
    protected override void Dispose(bool disposing)
    {
        if (disposing && BeginAbort())
        {
            // The token source is disposed once the staged write has finished with it.
            _ = _upload.ContinueWith(_ => _cancel.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    private void EnsureOpen()
    {
        if (Volatile.Read(ref _state) != 0)
            throw new ObjectDisposedException(nameof(StorageWriteStream), "The write was already committed or aborted.");
    }

    /// <summary>Carries the storage error that stopped a write.</summary>
    private sealed class StorageWriteException(Error error) : Exception(error.Message)
    {
        public Error Error { get; } = error;
    }
}

/// <summary>Opens push-style writes on any storage connection.</summary>
public static class StorageWriteExtensions
{
    /// <summary>
    /// Opens a stream to write a file's content into, committed with <see cref="StorageWriteStream.CommitAsync"/>.
    /// Overwrite, conflict (<c>Fail</c>, <c>Overwrite</c>, <c>Skip</c> and the conditional policies are decided
    /// here, before any byte is written), <c>Condition</c>, <c>ExpectedLength</c>, <c>Verify</c>, and
    /// <c>ExpectedSha256</c> apply as for uploads; <c>Resume</c> does not, because the content is not replayable.
    /// </summary>
    /// <param name="storage">Destination connection.</param>
    /// <param name="path">Destination path.</param>
    /// <param name="options">Upload options.</param>
    /// <param name="cancellationToken">Token that aborts the write.</param>
    /// <returns>The stream, or why writing cannot start (for example, the destination exists).</returns>
    public static async Task<Result<StorageWriteStream>> OpenWriteAsync(
        this IStorageService storage,
        string path,
        StorageUploadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        options ??= new StorageUploadOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<StorageWriteStream>.Failure(validation.Error!);
        if (options.ConflictPolicy == StorageConflictPolicy.Resume)
            return Result<StorageWriteStream>.Failure(StorageErrors.InvalidContent("A streamed write cannot be resumed; use UploadAsync with a seekable stream."));
        var normalized = StoragePath.Normalize(path);
        if (normalized.IsFailure) return Result<StorageWriteStream>.Failure(normalized.Error!);
        path = normalized.Value!;
        var overwrite = options.Overwrite;
        if (options.ConflictPolicy is { } policy)
        {
            var decision = await StorageConflictResolver.ResolveAsync(
                storage, path, policy, options.Overwrite, options.ExpectedLength, options.SourceLastModified, cancellationToken).ConfigureAwait(false);
            if (decision.IsFailure) return Result<StorageWriteStream>.Failure(decision.Error!);
            if (decision.Value.Skip)
                return Result<StorageWriteStream>.Failure(StorageErrors.Conflict(
                    $"The destination '{path}' exists and the conflict policy skips it.",
                    $"skipReason={StorageConflictResolver.SkipReasonFor(policy)}"));
            path = decision.Value.Path;
            overwrite = decision.Value.Overwrite;
        }
        if (!overwrite)
        {
            var exists = await storage.ExistsAsync(path, cancellationToken).ConfigureAwait(false);
            if (exists.IsFailure) return Result<StorageWriteStream>.Failure(exists.Error!);
            if (exists.Value) return Result<StorageWriteStream>.Failure(StorageErrors.Conflict($"The destination '{path}' already exists."));
        }
        if (options.Condition is { IsEmpty: false } condition)
        {
            var check = await StagedWriter.CheckConditionAsync(storage, path, condition, cancellationToken).ConfigureAwait(false);
            if (check.IsFailure) return Result<StorageWriteStream>.Failure(check.Error!);
        }
        return Result<StorageWriteStream>.Success(new StorageWriteStream(
            storage, path, options with { ConflictPolicy = null }, overwrite, cancellationToken));
    }
}
