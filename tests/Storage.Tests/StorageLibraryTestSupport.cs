using System.Diagnostics.CodeAnalysis;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Providers;
using CodeLogic.Core.Configuration;
using CodeLogic.Core.Events;
using CodeLogic.Core.Localization;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using CodeLogic.Framework.Libraries;

namespace Storage.Tests;

internal static class StorageLibraryTestSupport
{
    public static LibraryContext CreateContext(string root, IEventBus? events = null)
    {
        Directory.CreateDirectory(root);
        var configDirectory = Path.Combine(root, "config");
        var localizationDirectory = Path.Combine(root, "localization");
        var logsDirectory = Path.Combine(root, "logs");
        var dataDirectory = Path.Combine(root, "data");
        return new LibraryContext
        {
            LibraryId = "CL.Storage",
            LibraryDirectory = root,
            ConfigDirectory = configDirectory,
            LocalizationDirectory = localizationDirectory,
            LogsDirectory = logsDirectory,
            DataDirectory = dataDirectory,
            Logger = new TestLogger(),
            Configuration = new ConfigurationManager(configDirectory),
            Localization = new LocalizationManager(localizationDirectory),
            Events = events ?? new EventBus()
        };
    }

    public static async Task InitializeAsync(
        global::CL.Storage.StorageLibrary library,
        LibraryContext context,
        Action<StorageConfig>? configureStorage = null,
        Action<LocalStorageConfig>? configureLocal = null)
    {
        await library.OnConfigureAsync(context);
        await context.Configuration.LoadAllAsync();
        configureStorage?.Invoke(context.Configuration.Get<StorageConfig>());
        configureLocal?.Invoke(context.Configuration.Get<LocalStorageConfig>());
        await library.OnInitializeAsync(context);
    }
}

internal sealed class TestDirectory : IDisposable
{
    public TestDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cl-storage-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string CreateDirectory(string name)
    {
        var path = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch { }
    }
}

internal sealed class TestLogger : ILogger
{
    public List<string> Errors { get; } = [];
    public void Trace(string message) { }
    public void Debug(string message) { }
    public void Info(string message) { }
    public void Warning(string message) { }
    public void Error(string message, Exception? exception = null) => Errors.Add(message);
    public void Critical(string message, Exception? exception = null) => Errors.Add(message);
}

internal sealed class FakeStorageBackend :
    IStorageBackend,
    IStorageMetadataService,
    IStorageTagService,
    IStorageSignedUrlService,
    IStorageVersionService
{
    private readonly string _connectionId;
    private readonly Func<string>? _connectionIdGetter;
    private readonly object? _nativeClient;
    private Func<CancellationToken, Task<Result>> _health;
    private readonly Func<string, CancellationToken, Task<Result<StorageItem>>>? _getInfo;
    private readonly Func<string, CancellationToken, Task<Result<bool>>>? _exists;
    private readonly Func<string, StorageListOptions?, CancellationToken, Task<Result<StoragePage>>>? _list;
    private readonly Func<string, CancellationToken, Task<Result>>? _createDirectory;
    private readonly Func<string, CancellationToken, Task<Result<StorageItem>>>? _upload;
    private readonly Func<string, Stream, StorageUploadOptions?, CancellationToken, Task<Result<StorageItem>>>? _uploadStream;
    private readonly Func<string, CancellationToken, Task<Result<Stream>>>? _download;
    private readonly Func<string, CancellationToken, Task<Result>>? _delete;
    private readonly Func<string, string, CancellationToken, Task<Result>>? _copy;
    private readonly Func<string, string, CancellationToken, Task<Result>>? _move;
    private readonly Func<string, CancellationToken, Task<Result<IReadOnlyDictionary<string, string>>>>? _getMetadata;
    private readonly Func<string, IReadOnlyDictionary<string, string>, StorageMetadataUpdateOptions?, CancellationToken, Task<Result<StorageItem>>>? _setMetadata;
    private readonly Func<string, CancellationToken, Task<Result<IReadOnlyDictionary<string, string>>>>? _getTags;
    private readonly Func<string, IReadOnlyDictionary<string, string>, StorageTagUpdateOptions?, CancellationToken, Task<Result<StorageItem>>>? _setTags;
    private readonly Func<string, StorageSignedUrlOptions?, CancellationToken, Task<Result<StorageSignedUrl>>>? _createSignedUrl;
    private readonly Func<string, StorageVersionListOptions?, CancellationToken, Task<Result<StorageVersionPage>>>? _listVersions;
    private readonly Func<string, string, CancellationToken, Task<Result>>? _deleteVersion;
    private readonly Func<ValueTask>? _dispose;
    private int _disposeCount;
    private int _sessionReleases;

    public FakeStorageBackend(
        string connectionId,
        StorageProvider provider = StorageProvider.Local,
        string root = "/fake",
        object? nativeClient = null,
        Func<CancellationToken, Task<Result>>? health = null,
        Func<string, CancellationToken, Task<Result<StorageItem>>>? upload = null,
        Func<string, CancellationToken, Task<Result<Stream>>>? download = null,
        Func<string, CancellationToken, Task<Result>>? delete = null,
        Func<string, string, CancellationToken, Task<Result>>? copy = null,
        Func<string, string, CancellationToken, Task<Result>>? move = null,
        Func<ValueTask>? dispose = null,
        Func<string>? connectionIdGetter = null,
        Func<string, CancellationToken, Task<Result<StorageItem>>>? getInfo = null,
        Func<string, CancellationToken, Task<Result<bool>>>? exists = null,
        Func<string, StorageListOptions?, CancellationToken, Task<Result<StoragePage>>>? list = null,
        Func<string, CancellationToken, Task<Result>>? createDirectory = null,
        Func<string, Stream, StorageUploadOptions?, CancellationToken, Task<Result<StorageItem>>>? uploadStream = null,
        StorageCapabilities? capabilities = null,
        Func<string, CancellationToken, Task<Result<IReadOnlyDictionary<string, string>>>>? getMetadata = null,
        Func<string, IReadOnlyDictionary<string, string>, StorageMetadataUpdateOptions?, CancellationToken, Task<Result<StorageItem>>>? setMetadata = null,
        Func<string, StorageSignedUrlOptions?, CancellationToken, Task<Result<StorageSignedUrl>>>? createSignedUrl = null,
        Func<string, StorageVersionListOptions?, CancellationToken, Task<Result<StorageVersionPage>>>? listVersions = null,
        Func<string, string, CancellationToken, Task<Result>>? deleteVersion = null,
        Func<string, CancellationToken, Task<Result<IReadOnlyDictionary<string, string>>>>? getTags = null,
        Func<string, IReadOnlyDictionary<string, string>, StorageTagUpdateOptions?, CancellationToken, Task<Result<StorageItem>>>? setTags = null)
    {
        _connectionId = connectionId;
        _connectionIdGetter = connectionIdGetter;
        Provider = provider;
        Root = root;
        _nativeClient = nativeClient;
        _health = health ?? (_ => Task.FromResult(Result.Success()));
        _getInfo = getInfo;
        _exists = exists;
        _list = list;
        _createDirectory = createDirectory;
        _upload = upload;
        _uploadStream = uploadStream;
        _download = download;
        _delete = delete;
        _copy = copy;
        _move = move;
        _getMetadata = getMetadata;
        _setMetadata = setMetadata;
        _getTags = getTags;
        _setTags = setTags;
        _createSignedUrl = createSignedUrl;
        _listVersions = listVersions;
        _deleteVersion = deleteVersion;
        _dispose = dispose;
        Capabilities = capabilities ?? new(true, true, true, true, true, true);
    }

    public string ConnectionId => _connectionIdGetter?.Invoke() ?? _connectionId;
    public StorageProvider Provider { get; }
    public string Root { get; }
    public StorageCapabilities Capabilities { get; }
    public int DisposeCount => Volatile.Read(ref _disposeCount);
    public int SessionReleases => Volatile.Read(ref _sessionReleases);

    public void SetHealth(Func<CancellationToken, Task<Result>> health) =>
        _health = health ?? throw new ArgumentNullException(nameof(health));

    public Task<Result<StorageItem>> GetInfoAsync(string path, CancellationToken cancellationToken = default) =>
        _getInfo?.Invoke(path, cancellationToken) ??
        Task.FromResult(Result<StorageItem>.Failure(StorageErrors.NotFound("missing")));

    public Task<Result<bool>> ExistsAsync(string path, CancellationToken cancellationToken = default) =>
        _exists?.Invoke(path, cancellationToken) ?? Task.FromResult(Result<bool>.Success(false));

    public Task<Result<StoragePage>> ListAsync(string path, StorageListOptions? options = null, CancellationToken cancellationToken = default) =>
        _list?.Invoke(path, options, cancellationToken) ??
        Task.FromResult(Result<StoragePage>.Success(new StoragePage([], null)));

    public Task<Result> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
        _createDirectory?.Invoke(path, cancellationToken) ?? Task.FromResult(Result.Success());

    public Task<Result<StorageItem>> UploadAsync(string path, Stream source, StorageUploadOptions? options = null, CancellationToken cancellationToken = default) =>
        _uploadStream?.Invoke(path, source, options, cancellationToken) ?? UploadCoreAsync(path, cancellationToken);

    public Task<Result<StorageItem>> UploadBytesAsync(string path, byte[] content, StorageUploadOptions? options = null, CancellationToken cancellationToken = default) =>
        UploadCoreAsync(path, cancellationToken);

    public Task<Result<Stream>> DownloadAsync(string path, StorageDownloadOptions? options = null, CancellationToken cancellationToken = default) =>
        _download?.Invoke(path, cancellationToken) ?? Task.FromResult(Result<Stream>.Success(new MemoryStream()));

    public Task<Result<byte[]>> DownloadBytesAsync(string path, StorageDownloadOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<byte[]>.Success([]));

    public Task<Result> DeleteAsync(string path, StorageDeleteOptions? options = null, CancellationToken cancellationToken = default) =>
        _delete?.Invoke(path, cancellationToken) ?? Task.FromResult(Result.Success());

    public Task<Result> CopyAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default) =>
        _copy?.Invoke(sourcePath, destinationPath, cancellationToken) ?? Task.FromResult(Result.Success());

    public Task<Result> MoveAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default) =>
        _move?.Invoke(sourcePath, destinationPath, cancellationToken) ?? Task.FromResult(Result.Success());

    public Task<Result<IReadOnlyDictionary<string, string>>> GetMetadataAsync(string path, CancellationToken cancellationToken = default) =>
        _getMetadata?.Invoke(path, cancellationToken) ??
        Task.FromResult(Result<IReadOnlyDictionary<string, string>>.Failure(StorageErrors.Unsupported("metadata unsupported")));

    public Task<Result<StorageItem>> SetMetadataAsync(
        string path,
        IReadOnlyDictionary<string, string> metadata,
        StorageMetadataUpdateOptions? options = null,
        CancellationToken cancellationToken = default) =>
        _setMetadata?.Invoke(path, metadata, options, cancellationToken) ??
        Task.FromResult(Result<StorageItem>.Failure(StorageErrors.Unsupported("metadata unsupported")));

    public Task<Result<IReadOnlyDictionary<string, string>>> GetTagsAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        _getTags?.Invoke(path, cancellationToken) ??
        Task.FromResult(Result<IReadOnlyDictionary<string, string>>.Failure(StorageErrors.Unsupported("tags unsupported")));

    public Task<Result<StorageItem>> SetTagsAsync(
        string path,
        IReadOnlyDictionary<string, string> tags,
        StorageTagUpdateOptions? options = null,
        CancellationToken cancellationToken = default) =>
        _setTags?.Invoke(path, tags, options, cancellationToken) ??
        Task.FromResult(Result<StorageItem>.Failure(StorageErrors.Unsupported("tags unsupported")));

    public Task<Result<StorageSignedUrl>> CreateSignedUrlAsync(
        string path,
        StorageSignedUrlOptions? options = null,
        CancellationToken cancellationToken = default) =>
        _createSignedUrl?.Invoke(path, options, cancellationToken) ??
        Task.FromResult(Result<StorageSignedUrl>.Failure(StorageErrors.Unsupported("signed URLs unsupported")));

    public Task<Result<StorageVersionPage>> ListVersionsAsync(
        string path,
        StorageVersionListOptions? options = null,
        CancellationToken cancellationToken = default) =>
        _listVersions?.Invoke(path, options, cancellationToken) ??
        Task.FromResult(Result<StorageVersionPage>.Failure(StorageErrors.Unsupported("versions unsupported")));

    public Task<Result> DeleteVersionAsync(
        string path,
        string versionId,
        CancellationToken cancellationToken = default) =>
        _deleteVersion?.Invoke(path, versionId, cancellationToken) ??
        Task.FromResult(Result.Failure(StorageErrors.Unsupported("versions unsupported")));

    public Task<Result> CheckHealthAsync(CancellationToken cancellationToken = default) => _health(cancellationToken);

    public bool TryGetNativeClient<TClient>([NotNullWhen(true)] out TClient? client) where TClient : class
    {
        client = _nativeClient as TClient;
        return client is not null;
    }

    public Task<Result<NativeConnectionLease<TClient>>> OpenNativeConnectionAsync<TClient>(CancellationToken cancellationToken = default)
        where TClient : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_nativeClient is not TClient client)
            return Task.FromResult(Result<NativeConnectionLease<TClient>>.Failure(StorageErrors.Unsupported("wrong native session type")));
        return Task.FromResult(Result<NativeConnectionLease<TClient>>.Success(
            new NativeConnectionLease<TClient>(client, _ =>
            {
                Interlocked.Increment(ref _sessionReleases);
                return ValueTask.CompletedTask;
            })));
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        return _dispose?.Invoke() ?? ValueTask.CompletedTask;
    }

    private Task<Result<StorageItem>> UploadCoreAsync(string path, CancellationToken cancellationToken) =>
        _upload?.Invoke(path, cancellationToken) ?? Task.FromResult(Result<StorageItem>.Success(new StorageItem
        {
            Path = path,
            Name = System.IO.Path.GetFileName(path),
            ItemType = StorageItemType.File
        }));
}

internal sealed class ThrowingEventBus : IEventBus
{
    public List<IEvent> Published { get; } = [];
    public void Publish<T>(T @event) where T : IEvent => throw new InvalidOperationException("event failure");
    public Task PublishAsync<T>(T @event) where T : IEvent
    {
        Published.Add(@event);
        throw new InvalidOperationException("event failure");
    }
    public IEventSubscription Subscribe<T>(Action<T> handler) where T : IEvent => throw new NotSupportedException();
    public IEventSubscription SubscribeAsync<T>(Func<T, Task> handler) where T : IEvent => throw new NotSupportedException();
}

internal sealed class StopDuringPublishEventBus(Func<Task> stop) : IEventBus
{
    public List<IEvent> Published { get; } = [];

    public void Publish<T>(T @event) where T : IEvent => throw new NotSupportedException();

    public async Task PublishAsync<T>(T @event) where T : IEvent
    {
        await stop();
        Published.Add(@event);
        throw new InvalidOperationException("event failure after stop");
    }

    public IEventSubscription Subscribe<T>(Action<T> handler) where T : IEvent => throw new NotSupportedException();
    public IEventSubscription SubscribeAsync<T>(Func<T, Task> handler) where T : IEvent => throw new NotSupportedException();
}

internal sealed class FakeStorageBackendFactory(
    Func<string, object, IStorageBackend> create) : IStorageBackendFactory
{
    public Type ConfigurationType => typeof(LocalConnectionConfig);
    public StorageProvider Provider => StorageProvider.Local;
    public IStorageBackend Create(string connectionId, object configuration, long maxBufferedDownloadBytes) =>
        create(connectionId, configuration);
}
