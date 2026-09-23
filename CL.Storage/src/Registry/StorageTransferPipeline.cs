using System.Runtime.CompilerServices;
using CL.Storage.Abstractions;
using CL.Storage.Models;
using CL.Storage.Providers;
using CodeLogic.Core.Results;

namespace CL.Storage.Registry;

/// <summary>Bandwidth budgets attached to one backend: its own and the library-wide ones.</summary>
internal sealed record TransferLimits(TokenBucket? Upload, TokenBucket? Download, TokenBucket? TotalUpload, TokenBucket? TotalDownload)
{
    public static TransferLimits None { get; } = new(null, null, null, null);
    public bool LimitsUploads => Upload is not null || TotalUpload is not null;
    public bool LimitsDownloads => Download is not null || TotalDownload is not null;
}

/// <summary>
/// The shared entry every backend's upload and download passes through: progress reporting, speed limits,
/// and conflict policies. Backends call it themselves, so the behaviour is the same whether a backend is
/// used through the library or on its own, and for relayed transfers between connections.
/// </summary>
internal static class StorageTransferPipeline
{
    private static readonly ConditionalWeakTable<object, TransferLimits> Limits = new();

    /// <summary>Attaches bandwidth limits to a backend; null or non-positive values mean unlimited.</summary>
    public static void SetLimits(object backend, long? uploadBytesPerSecond, long? downloadBytesPerSecond, TransferLimits? library = null)
    {
        Limits.AddOrUpdate(backend, new TransferLimits(
            Bucket(uploadBytesPerSecond),
            Bucket(downloadBytesPerSecond),
            library?.TotalUpload,
            library?.TotalDownload));
    }

    /// <summary>Creates the library-wide budgets that every connection shares.</summary>
    public static TransferLimits LibraryLimits(long? totalUploadBytesPerSecond, long? totalDownloadBytesPerSecond) =>
        new(null, null, Bucket(totalUploadBytesPerSecond), Bucket(totalDownloadBytesPerSecond));

    public static TransferLimits LimitsFor(object backend) => Limits.TryGetValue(backend, out var limits) ? limits : TransferLimits.None;

    /// <summary>Whether an upload call must go through <see cref="UploadAsync"/> first.</summary>
    public static bool Applies(object backend, StorageUploadOptions? options) =>
        options?.PipelineApplied != true &&
        (options?.ConflictPolicy is not null || options?.Progress is not null || LimitsFor(backend).LimitsUploads);

    public static async Task<Result<StorageItem>> UploadAsync(
        IStorageService destination,
        string path,
        Stream source,
        StorageUploadOptions? options,
        CancellationToken cancellationToken)
    {
        options ??= new StorageUploadOptions();
        var limits = LimitsFor(destination);
        Stream stream = source;
        if (options.Progress is not null || limits.LimitsUploads)
        {
            long? total = null;
            if (source.CanSeek)
            {
                try { total = Math.Max(0, source.Length - source.Position); }
                catch (NotSupportedException) { }
            }
            stream = new MeteredStream(source, options.Progress, total, path, leaveOpen: true, limits.Upload, limits.TotalUpload);
        }
        var inner = options with { Progress = null, PipelineApplied = true };
        try
        {
            return inner.ConflictPolicy is not null
                ? await StorageConflictResolver.UploadAsync(destination, path, stream, inner, cancellationToken).ConfigureAwait(false)
                : await destination.UploadAsync(path, stream, inner, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!ReferenceEquals(stream, source)) await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Wraps a successful download in progress reporting and speed limits when requested.</summary>
    /// <summary>
    /// Wraps a download for progress and speed limits. With a progress sink and no known length, the item's
    /// size is looked up once so reports carry <see cref="StorageTransferProgress.TotalBytes"/>.
    /// </summary>
    public static async Task<Result<Stream>> MeterAsync(
        IStorageBackend backend,
        string path,
        Result<Stream> download,
        StorageDownloadOptions? options,
        CancellationToken cancellationToken)
    {
        if (download.IsFailure) return download;
        var limits = LimitsFor(backend);
        if (options?.Progress is null && !limits.LimitsDownloads) return download;
        var total = options?.Length;
        if (total is null && options?.Progress is not null)
        {
            var stream = download.Value!;
            if (stream.CanSeek)
            {
                total = stream.Length - stream.Position;
            }
            else
            {
                var info = await backend.GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
                if (info.IsSuccess && info.Value!.Size is { } size)
                    total = Math.Max(0, size - (options.Offset));
            }
        }
        return Result<Stream>.Success(new MeteredStream(
            download.Value!,
            options?.Progress,
            total,
            path,
            leaveOpen: false,
            limits.Download,
            limits.TotalDownload));
    }

    private static TokenBucket? Bucket(long? bytesPerSecond) => bytesPerSecond is > 0 ? new TokenBucket(bytesPerSecond.Value) : null;
}
