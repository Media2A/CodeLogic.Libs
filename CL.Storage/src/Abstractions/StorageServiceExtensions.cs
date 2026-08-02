using System.Runtime.CompilerServices;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Abstractions;

/// <summary>Provider-neutral streaming and batch helpers for <see cref="IStorageService"/>.</summary>
public static class StorageServiceExtensions
{
    private const int DefaultSerializedContentLimit = 16 * 1024 * 1024;

    /// <summary>Uploads a caller-owned stream while reporting bytes consumed by the provider.</summary>
    public static async Task<Result<StorageItem>> UploadWithProgressAsync(
        this IStorageService storage,
        string path,
        Stream source,
        IProgress<StorageTransferProgress> progress,
        StorageUploadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(progress);
        cancellationToken.ThrowIfCancellationRequested();
        long? total = null;
        if (source.CanSeek)
        {
            try { total = Math.Max(0, source.Length - source.Position); }
            catch (NotSupportedException) { }
        }
        await using var tracked = new ProgressReadStream(source, progress, total, leaveOpen: true);
        return await storage.UploadAsync(path, tracked, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns an owned download stream that reports bytes as the caller reads it.</summary>
    public static async Task<Result<Stream>> DownloadWithProgressAsync(
        this IStorageService storage,
        string path,
        IProgress<StorageTransferProgress> progress,
        StorageDownloadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(progress);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new StorageDownloadOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<Stream>.Failure(validation.Error!);

        long? total = options.Length;
        if (!total.HasValue)
        {
            var info = await storage.GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
            if (info.IsSuccess && info.Value!.Size.HasValue)
                total = Math.Max(0, info.Value.Size.Value - options.Offset);
        }
        var download = await storage.DownloadAsync(path, options, cancellationToken).ConfigureAwait(false);
        return download.IsSuccess
            ? Result<Stream>.Success(new ProgressReadStream(download.Value!, progress, total, leaveOpen: false))
            : Result<Stream>.Failure(download.Error!);
    }

    /// <summary>Streams a local file into storage while keeping ownership of the local stream internal.</summary>
    public static async Task<Result<StorageItem>> UploadFileAsync(
        this IStorageService storage,
        string destinationPath,
        string sourceFilePath,
        StorageUploadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (string.IsNullOrWhiteSpace(sourceFilePath))
            return Result<StorageItem>.Failure(StorageErrors.InvalidPath("A source file path is required."));
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var source = new FileStream(
                Path.GetFullPath(sourceFilePath),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                65_536,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await storage.UploadAsync(destinationPath, source, options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            return Result<StorageItem>.Failure(StorageErrors.FromException(error, "Open local upload file"));
        }
    }

    /// <summary>
    /// Streams an item to a sibling staging file and atomically commits it, preserving an existing
    /// destination when the download fails or is cancelled.
    /// </summary>
    public static async Task<Result<FileInfo>> DownloadToFileAsync(
        this IStorageService storage,
        string sourcePath,
        string destinationFilePath,
        StorageDownloadOptions? options = null,
        bool overwrite = true,
        bool createParents = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (string.IsNullOrWhiteSpace(destinationFilePath))
            return Result<FileInfo>.Failure(StorageErrors.InvalidPath("A destination file path is required."));
        cancellationToken.ThrowIfCancellationRequested();

        string? stagingPath = null;
        try
        {
            var destination = Path.GetFullPath(destinationFilePath);
            var parent = Path.GetDirectoryName(destination);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(Path.GetFileName(destination)))
                return Result<FileInfo>.Failure(StorageErrors.InvalidPath("The destination must identify a file."));
            if (createParents)
                Directory.CreateDirectory(parent);
            else if (!Directory.Exists(parent))
                return Result<FileInfo>.Failure(StorageErrors.NotFound("The destination parent directory was not found."));

            var download = await storage.DownloadAsync(sourcePath, options, cancellationToken).ConfigureAwait(false);
            if (download.IsFailure) return Result<FileInfo>.Failure(download.Error!);

            stagingPath = Path.Combine(parent, $".clstorage-download-{Guid.NewGuid():N}.tmp");
            await using (var source = download.Value!)
            await using (var destinationStream = new FileStream(
                stagingPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                65_536,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destinationStream, 65_536, cancellationToken).ConfigureAwait(false);
                await destinationStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                destinationStream.Flush(flushToDisk: true);
            }

            File.Move(stagingPath, destination, overwrite);
            stagingPath = null;
            return Result<FileInfo>.Success(new FileInfo(destination));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (IOException) when (!overwrite && destinationFilePath is not null && File.Exists(Path.GetFullPath(destinationFilePath)))
        {
            return Result<FileInfo>.Failure(StorageErrors.Conflict("The local download destination already exists."));
        }
        catch (Exception error)
        {
            return Result<FileInfo>.Failure(StorageErrors.FromException(error, "Write local download file"));
        }
        finally
        {
            if (stagingPath is not null)
            {
                try { File.Delete(stagingPath); }
                catch { }
            }
        }
    }

    /// <summary>Downloads bounded text content.</summary>
    public static async Task<Result<string>> ReadTextAsync(
        this IStorageService storage,
        string path,
        StorageDownloadOptions? options = null,
        Encoding? encoding = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        encoding ??= new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var bytes = await storage.DownloadBytesAsync(path, options, cancellationToken).ConfigureAwait(false);
        if (bytes.IsFailure) return Result<string>.Failure(bytes.Error!);
        try
        {
            var content = bytes.Value!;
            var preamble = encoding.GetPreamble();
            var offset = preamble.Length > 0 && content.AsSpan().StartsWith(preamble)
                ? preamble.Length
                : 0;
            var text = encoding.GetString(content, offset, content.Length - offset);
            return Result<string>.Success(text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text);
        }
        catch (DecoderFallbackException)
        {
            return Result<string>.Failure(StorageErrors.InvalidContent("The stored content is not valid text in the selected encoding."));
        }
    }

    /// <summary>Encodes and uploads text with an explicit serialization bound.</summary>
    public static Task<Result<StorageItem>> WriteTextAsync(
        this IStorageService storage,
        string path,
        string content,
        StorageUploadOptions? options = null,
        Encoding? encoding = null,
        int maxEncodedBytes = DefaultSerializedContentLimit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(content);
        encoding ??= new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        if (maxEncodedBytes <= 0)
            return Task.FromResult(Result<StorageItem>.Failure(StorageErrors.InvalidPath(
                "maxEncodedBytes must be greater than zero.")));
        var byteCount = encoding.GetByteCount(content);
        if (byteCount > maxEncodedBytes)
            return Task.FromResult(Result<StorageItem>.Failure(StorageErrors.TooLarge(
                $"Encoded text exceeds the {maxEncodedBytes} byte limit.")));
        var bytes = encoding.GetBytes(content);
        return storage.UploadBytesAsync(path, bytes, options, cancellationToken);
    }

    /// <summary>Downloads and deserializes bounded JSON content.</summary>
    public static async Task<Result<T>> ReadJsonAsync<T>(
        this IStorageService storage,
        string path,
        StorageDownloadOptions? downloadOptions = null,
        JsonSerializerOptions? jsonOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        var bytes = await storage.DownloadBytesAsync(path, downloadOptions, cancellationToken).ConfigureAwait(false);
        if (bytes.IsFailure) return Result<T>.Failure(bytes.Error!);
        try
        {
            var value = JsonSerializer.Deserialize<T>(bytes.Value!, jsonOptions);
            return value is null
                ? Result<T>.Failure(StorageErrors.InvalidContent("The JSON content resolved to null."))
                : Result<T>.Success(value);
        }
        catch (JsonException)
        {
            return Result<T>.Failure(StorageErrors.InvalidContent("The stored content is not valid JSON for the requested type."));
        }
    }

    /// <summary>Serializes JSON into a bounded buffer and uploads it.</summary>
    public static async Task<Result<StorageItem>> WriteJsonAsync<T>(
        this IStorageService storage,
        string path,
        T value,
        StorageUploadOptions? uploadOptions = null,
        JsonSerializerOptions? jsonOptions = null,
        int maxSerializedBytes = DefaultSerializedContentLimit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (maxSerializedBytes <= 0)
            return Result<StorageItem>.Failure(StorageErrors.InvalidPath(
                "maxSerializedBytes must be greater than zero."));
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var content = new BoundedMemoryStream(maxSerializedBytes);
            await JsonSerializer.SerializeAsync(content, value, jsonOptions, cancellationToken).ConfigureAwait(false);
            content.Position = 0;
            return await storage.UploadAsync(path, content, uploadOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (StorageBufferLimitException)
        {
            return Result<StorageItem>.Failure(StorageErrors.TooLarge(
                $"Serialized JSON exceeds the {maxSerializedBytes} byte limit."));
        }
        catch (JsonException)
        {
            return Result<StorageItem>.Failure(StorageErrors.InvalidContent(
                "The value could not be serialized as JSON."));
        }
    }

    /// <summary>
    /// Streams an item through a client-side digest without buffering its content. MD5 is available
    /// for interoperability; SHA-256 or stronger should be used for security-sensitive verification.
    /// </summary>
    public static async Task<Result<StorageChecksum>> ComputeChecksumAsync(
        this IStorageService storage,
        string path,
        StorageChecksumAlgorithm algorithm = StorageChecksumAlgorithm.Sha256,
        StorageDownloadOptions? options = null,
        IProgress<StorageTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(algorithm))
            return Result<StorageChecksum>.Failure(StorageErrors.InvalidPath(
                "The checksum algorithm is invalid."));
        options ??= new StorageDownloadOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<StorageChecksum>.Failure(validation.Error!);

        var download = await storage.DownloadAsync(path, options, cancellationToken).ConfigureAwait(false);
        if (download.IsFailure) return Result<StorageChecksum>.Failure(download.Error!);
        var buffer = ArrayPool<byte>.Shared.Rent(65_536);
        try
        {
            await using var source = download.Value!;
            using var hash = IncrementalHash.CreateHash(ToHashAlgorithmName(algorithm));
            long processed = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, 65_536), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;
                hash.AppendData(buffer, 0, read);
                processed = checked(processed + read);
                progress?.Report(new StorageTransferProgress(processed, options.Length, false));
            }
            var digest = hash.GetHashAndReset();
            progress?.Report(new StorageTransferProgress(processed, options.Length, true));
            return Result<StorageChecksum>.Success(new StorageChecksum(
                algorithm,
                Convert.ToHexStringLower(digest),
                processed));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            return Result<StorageChecksum>.Failure(StorageErrors.FromException(
                error,
                "Compute storage checksum"));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Computes a digest and compares it to hexadecimal expected bytes in constant time.</summary>
    public static async Task<Result<StorageChecksumVerification>> VerifyChecksumAsync(
        this IStorageService storage,
        string path,
        string expectedHexValue,
        StorageChecksumAlgorithm algorithm = StorageChecksumAlgorithm.Sha256,
        StorageDownloadOptions? options = null,
        IProgress<StorageTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (string.IsNullOrWhiteSpace(expectedHexValue) || expectedHexValue.Any(char.IsWhiteSpace))
            return Result<StorageChecksumVerification>.Failure(StorageErrors.InvalidPath(
                "An uninterrupted hexadecimal checksum is required."));
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(expectedHexValue);
        }
        catch (FormatException)
        {
            return Result<StorageChecksumVerification>.Failure(StorageErrors.InvalidPath(
                "The expected checksum is not valid hexadecimal."));
        }

        var expectedLength = ChecksumLength(algorithm);
        if (expectedLength is null)
            return Result<StorageChecksumVerification>.Failure(StorageErrors.InvalidPath(
                "The checksum algorithm is invalid."));
        if (expected.Length != expectedLength.Value)
            return Result<StorageChecksumVerification>.Failure(StorageErrors.InvalidPath(
                $"The expected {algorithm} checksum must contain {expectedLength.Value} bytes."));

        var actual = await storage.ComputeChecksumAsync(
            path,
            algorithm,
            options,
            progress,
            cancellationToken).ConfigureAwait(false);
        if (actual.IsFailure)
            return Result<StorageChecksumVerification>.Failure(actual.Error!);
        var actualBytes = Convert.FromHexString(actual.Value!.HexValue);
        return Result<StorageChecksumVerification>.Success(new StorageChecksumVerification(
            actual.Value,
            CryptographicOperations.FixedTimeEquals(actualBytes, expected)));
    }

    /// <summary>Reads user metadata when the connection advertises metadata reads.</summary>
    public static Task<Result<IReadOnlyDictionary<string, string>>> GetMetadataAsync(
        this IStorageService storage,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        cancellationToken.ThrowIfCancellationRequested();
        return storage is IStorageMetadataService metadata
            ? metadata.GetMetadataAsync(path, cancellationToken)
            : Task.FromResult(Result<IReadOnlyDictionary<string, string>>.Failure(
                StorageErrors.Unsupported("This storage connection does not support metadata operations.")));
    }

    /// <summary>Updates user metadata when the connection advertises metadata writes.</summary>
    public static Task<Result<StorageItem>> SetMetadataAsync(
        this IStorageService storage,
        string path,
        IReadOnlyDictionary<string, string> metadata,
        StorageMetadataUpdateOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(metadata);
        cancellationToken.ThrowIfCancellationRequested();
        return storage is IStorageMetadataService metadataService
            ? metadataService.SetMetadataAsync(path, metadata, options, cancellationToken)
            : Task.FromResult(Result<StorageItem>.Failure(
                StorageErrors.Unsupported("This storage connection does not support metadata updates.")));
    }

    /// <summary>Reads provider-native object tags when supported by the connection.</summary>
    public static Task<Result<IReadOnlyDictionary<string, string>>> GetTagsAsync(
        this IStorageService storage,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        cancellationToken.ThrowIfCancellationRequested();
        return storage is IStorageTagService tags
            ? tags.GetTagsAsync(path, cancellationToken)
            : Task.FromResult(Result<IReadOnlyDictionary<string, string>>.Failure(
                StorageErrors.Unsupported("This storage connection does not support object tags.")));
    }

    /// <summary>Merges or replaces provider-native object tags when supported by the connection.</summary>
    public static Task<Result<StorageItem>> SetTagsAsync(
        this IStorageService storage,
        string path,
        IReadOnlyDictionary<string, string> tags,
        StorageTagUpdateOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(tags);
        cancellationToken.ThrowIfCancellationRequested();
        return storage is IStorageTagService tagService
            ? tagService.SetTagsAsync(path, tags, options, cancellationToken)
            : Task.FromResult(Result<StorageItem>.Failure(
                StorageErrors.Unsupported("This storage connection does not support object tag updates.")));
    }

    /// <summary>Creates a temporary signed read/write URL when supported by the connection.</summary>
    public static Task<Result<StorageSignedUrl>> CreateSignedUrlAsync(
        this IStorageService storage,
        string path,
        StorageSignedUrlOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        cancellationToken.ThrowIfCancellationRequested();
        return storage is IStorageSignedUrlService signedUrls
            ? signedUrls.CreateSignedUrlAsync(path, options, cancellationToken)
            : Task.FromResult(Result<StorageSignedUrl>.Failure(
                StorageErrors.Unsupported("This storage connection does not support signed URLs.")));
    }

    /// <summary>Lists one page of versions for an exact object path.</summary>
    public static Task<Result<StorageVersionPage>> ListVersionsAsync(
        this IStorageService storage,
        string path,
        StorageVersionListOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        cancellationToken.ThrowIfCancellationRequested();
        return storage is IStorageVersionService versions
            ? versions.ListVersionsAsync(path, options, cancellationToken)
            : Task.FromResult(Result<StorageVersionPage>.Failure(
                StorageErrors.Unsupported("This storage connection does not support object versions.")));
    }

    /// <summary>Requests deletion of one exact object version, subject to provider retention policy.</summary>
    public static Task<Result> DeleteVersionAsync(
        this IStorageService storage,
        string path,
        string versionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        cancellationToken.ThrowIfCancellationRequested();
        return storage is IStorageVersionService versions
            ? versions.DeleteVersionAsync(path, versionId, cancellationToken)
            : Task.FromResult(Result.Failure(
                StorageErrors.Unsupported("This storage connection does not support object versions.")));
    }

    /// <summary>Enumerates every version page while rejecting repeated provider tokens.</summary>
    public static async IAsyncEnumerable<Result<StorageVersionPage>> EnumerateVersionPagesAsync(
        this IStorageService storage,
        string path,
        StorageVersionListOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        options ??= new StorageVersionListOptions();
        var validation = options.Validate();
        if (validation.IsFailure)
        {
            yield return Result<StorageVersionPage>.Failure(validation.Error!);
            yield break;
        }

        var continuationToken = options.ContinuationToken;
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(continuationToken))
            seenTokens.Add(continuationToken);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await storage.ListVersionsAsync(
                path,
                options with { ContinuationToken = continuationToken },
                cancellationToken).ConfigureAwait(false);
            yield return page;
            if (page.IsFailure)
                yield break;
            var next = page.Value!.ContinuationToken;
            if (string.IsNullOrEmpty(next))
                yield break;
            if (!seenTokens.Add(next))
            {
                yield return Result<StorageVersionPage>.Failure(StorageErrors.ProviderError(
                    "The storage provider repeated a continuation token while enumerating versions."));
                yield break;
            }
            continuationToken = next;
        }
    }

    /// <summary>
    /// Enumerates provider pages without buffering the complete listing. Provider-opaque continuation
    /// tokens are forwarded unchanged and repeated tokens terminate with a failed result.
    /// </summary>
    public static async IAsyncEnumerable<Result<StoragePage>> EnumeratePagesAsync(
        this IStorageService storage,
        string path,
        StorageListOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        options ??= new StorageListOptions();
        var validation = options.Validate();
        if (validation.IsFailure)
        {
            yield return Result<StoragePage>.Failure(validation.Error!);
            yield break;
        }

        var continuationToken = options.ContinuationToken;
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(continuationToken))
            seenTokens.Add(continuationToken);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await storage.ListAsync(
                path,
                options with { ContinuationToken = continuationToken },
                cancellationToken).ConfigureAwait(false);
            yield return page;
            if (page.IsFailure)
                yield break;

            var next = page.Value!.ContinuationToken;
            if (string.IsNullOrEmpty(next))
                yield break;
            if (!seenTokens.Add(next))
            {
                yield return Result<StoragePage>.Failure(StorageErrors.ProviderError(
                    "The storage provider repeated a continuation token while enumerating pages."));
                yield break;
            }
            continuationToken = next;
        }
    }

    /// <summary>
    /// Enumerates items one at a time. A page failure is emitted as one failed item result and ends
    /// enumeration, allowing callers to retain items from earlier successful pages.
    /// </summary>
    public static async IAsyncEnumerable<Result<StorageItem>> EnumerateItemsAsync(
        this IStorageService storage,
        string path,
        StorageListOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var page in storage.EnumeratePagesAsync(path, options, cancellationToken)
            .ConfigureAwait(false))
        {
            if (page.IsFailure)
            {
                yield return Result<StorageItem>.Failure(page.Error!);
                yield break;
            }
            foreach (var item in page.Value!.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return Result<StorageItem>.Success(item);
            }
        }
    }

    /// <summary>Gets item information concurrently while preserving the input order and per-item results.</summary>
    public static async Task<Result<IReadOnlyList<StorageBatchItemResult<StorageItem>>>> GetInfoBatchAsync(
        this IStorageService storage,
        IEnumerable<string> paths,
        StorageBatchOptions? batchOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(paths);
        batchOptions ??= new StorageBatchOptions();
        var inputs = paths.ToArray();
        var validation = ValidateBatch(batchOptions, inputs.Length);
        if (validation.IsFailure)
            return Result<IReadOnlyList<StorageBatchItemResult<StorageItem>>>.Failure(validation.Error!);

        var results = new StorageBatchItemResult<StorageItem>[inputs.Length];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, inputs.Length),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = batchOptions.MaxConcurrency,
                CancellationToken = cancellationToken
            },
            async (index, token) =>
            {
                Result<StorageItem> item;
                try
                {
                    item = await storage.GetInfoAsync(inputs[index], token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    item = Result<StorageItem>.Failure(StorageErrors.FromException(error, "Get batch item info"));
                }
                results[index] = new StorageBatchItemResult<StorageItem>(index, inputs[index], item);
            }).ConfigureAwait(false);
        return Result<IReadOnlyList<StorageBatchItemResult<StorageItem>>>.Success(Array.AsReadOnly(results));
    }

    /// <summary>Deletes paths concurrently while preserving the input order and per-item results.</summary>
    public static async Task<Result<IReadOnlyList<StorageBatchMutationResult>>> DeleteBatchAsync(
        this IStorageService storage,
        IEnumerable<string> paths,
        StorageDeleteOptions? deleteOptions = null,
        StorageBatchOptions? batchOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(paths);
        deleteOptions ??= new StorageDeleteOptions();
        batchOptions ??= new StorageBatchOptions();
        var inputs = paths.ToArray();
        var validation = ValidateBatch(batchOptions, inputs.Length);
        if (validation.IsFailure)
            return Result<IReadOnlyList<StorageBatchMutationResult>>.Failure(validation.Error!);
        validation = deleteOptions.Validate();
        if (validation.IsFailure)
            return Result<IReadOnlyList<StorageBatchMutationResult>>.Failure(validation.Error!);

        var results = new StorageBatchMutationResult[inputs.Length];
        await RunMutationBatchAsync(
            inputs.Length,
            batchOptions,
            cancellationToken,
            async (index, token) =>
            {
                Result result;
                try
                {
                    result = await storage.DeleteAsync(inputs[index], deleteOptions, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    result = Result.Failure(StorageErrors.FromException(error, "Delete batch item"));
                }
                results[index] = new StorageBatchMutationResult(index, inputs[index], null, result);
            }).ConfigureAwait(false);
        return Result<IReadOnlyList<StorageBatchMutationResult>>.Success(Array.AsReadOnly(results));
    }

    /// <summary>Copies path pairs concurrently while preserving the input order and per-item results.</summary>
    public static Task<Result<IReadOnlyList<StorageBatchMutationResult>>> CopyBatchAsync(
        this IStorageService storage,
        IEnumerable<StorageTransferRequest> transfers,
        StorageTransferOptions? transferOptions = null,
        StorageBatchOptions? batchOptions = null,
        CancellationToken cancellationToken = default) =>
        TransferBatchAsync(storage, transfers, transferOptions, batchOptions, move: false, cancellationToken);

    /// <summary>Moves path pairs concurrently while preserving the input order and per-item results.</summary>
    public static Task<Result<IReadOnlyList<StorageBatchMutationResult>>> MoveBatchAsync(
        this IStorageService storage,
        IEnumerable<StorageTransferRequest> transfers,
        StorageTransferOptions? transferOptions = null,
        StorageBatchOptions? batchOptions = null,
        CancellationToken cancellationToken = default) =>
        TransferBatchAsync(storage, transfers, transferOptions, batchOptions, move: true, cancellationToken);

    private static async Task<Result<IReadOnlyList<StorageBatchMutationResult>>> TransferBatchAsync(
        IStorageService storage,
        IEnumerable<StorageTransferRequest> transfers,
        StorageTransferOptions? transferOptions,
        StorageBatchOptions? batchOptions,
        bool move,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(transfers);
        transferOptions ??= new StorageTransferOptions();
        batchOptions ??= new StorageBatchOptions();
        var inputs = transfers.ToArray();
        var validation = ValidateBatch(batchOptions, inputs.Length);
        if (validation.IsFailure)
            return Result<IReadOnlyList<StorageBatchMutationResult>>.Failure(validation.Error!);
        validation = transferOptions.Validate();
        if (validation.IsFailure)
            return Result<IReadOnlyList<StorageBatchMutationResult>>.Failure(validation.Error!);
        if (inputs.Any(input => input is null))
            return Result<IReadOnlyList<StorageBatchMutationResult>>.Failure(StorageErrors.InvalidPath(
                "A transfer batch cannot contain a null request."));

        var results = new StorageBatchMutationResult[inputs.Length];
        await RunMutationBatchAsync(
            inputs.Length,
            batchOptions,
            cancellationToken,
            async (index, token) =>
            {
                var input = inputs[index];
                Result result;
                try
                {
                    result = move
                        ? await storage.MoveAsync(
                            input.SourcePath,
                            input.DestinationPath,
                            transferOptions,
                            token).ConfigureAwait(false)
                        : await storage.CopyAsync(
                            input.SourcePath,
                            input.DestinationPath,
                            transferOptions,
                            token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    result = Result.Failure(StorageErrors.FromException(
                        error,
                        move ? "Move batch item" : "Copy batch item"));
                }
                results[index] = new StorageBatchMutationResult(
                    index,
                    input.SourcePath,
                    input.DestinationPath,
                    result);
            }).ConfigureAwait(false);
        return Result<IReadOnlyList<StorageBatchMutationResult>>.Success(Array.AsReadOnly(results));
    }

    private static async Task RunMutationBatchAsync(
        int count,
        StorageBatchOptions options,
        CancellationToken cancellationToken,
        Func<int, CancellationToken, Task> operation)
    {
        await Parallel.ForEachAsync(
            Enumerable.Range(0, count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = options.MaxConcurrency,
                CancellationToken = cancellationToken
            },
            async (index, token) => await operation(index, token).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static Result ValidateBatch(StorageBatchOptions options, int count)
    {
        var validation = options.Validate();
        if (validation.IsFailure)
            return validation;
        return count <= options.MaxItems
            ? Result.Success()
            : Result.Failure(StorageErrors.TooLarge(
                $"The batch contains {count} items; the configured maximum is {options.MaxItems}."));
    }

    private static HashAlgorithmName ToHashAlgorithmName(StorageChecksumAlgorithm algorithm) => algorithm switch
    {
        StorageChecksumAlgorithm.Md5 => HashAlgorithmName.MD5,
        StorageChecksumAlgorithm.Sha256 => HashAlgorithmName.SHA256,
        StorageChecksumAlgorithm.Sha384 => HashAlgorithmName.SHA384,
        StorageChecksumAlgorithm.Sha512 => HashAlgorithmName.SHA512,
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm))
    };

    private static int? ChecksumLength(StorageChecksumAlgorithm algorithm) => algorithm switch
    {
        StorageChecksumAlgorithm.Md5 => 16,
        StorageChecksumAlgorithm.Sha256 => 32,
        StorageChecksumAlgorithm.Sha384 => 48,
        StorageChecksumAlgorithm.Sha512 => 64,
        _ => null
    };

    private sealed class ProgressReadStream(
        Stream inner,
        IProgress<StorageTransferProgress> progress,
        long? totalBytes,
        bool leaveOpen) : Stream
    {
        private long _bytesTransferred;
        private int _completed;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => totalBytes ?? throw new NotSupportedException();
        public override long Position
        {
            get => Interlocked.Read(ref _bytesTransferred);
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Observe(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Observe(inner.Read(buffer));

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            return Observe(read);
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            return Observe(read);
        }

        private int Observe(int read)
        {
            var transferred = read > 0
                ? Interlocked.Add(ref _bytesTransferred, read)
                : Interlocked.Read(ref _bytesTransferred);
            var complete = read == 0 || (totalBytes.HasValue && transferred >= totalBytes.Value);
            if (!complete || Interlocked.Exchange(ref _completed, 1) == 0)
                progress.Report(new StorageTransferProgress(transferred, totalBytes, complete));
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !leaveOpen)
                inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!leaveOpen)
                await inner.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }

    private sealed class BoundedMemoryStream(int maxBytes) : MemoryStream(Math.Min(maxBytes, 65_536))
    {
        private readonly int _maxBytes = maxBytes;

        public override void SetLength(long value)
        {
            if (value > _maxBytes) throw new StorageBufferLimitException();
            base.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacityFor(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacityFor(buffer.Length);
            base.Write(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            EnsureCapacityFor(count);
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            EnsureCapacityFor(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }

        public override void WriteByte(byte value)
        {
            EnsureCapacityFor(1);
            base.WriteByte(value);
        }

        private void EnsureCapacityFor(int count)
        {
            if (count < 0 || Position > _maxBytes - count || Math.Max(Length, Position + count) > _maxBytes)
                throw new StorageBufferLimitException();
        }
    }

    private sealed class StorageBufferLimitException : IOException { }
}
