using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Providers.Swift;

/// <summary>Root-scoped storage over the OpenStack Swift HTTP API.</summary>
public sealed class SwiftStorageBackend : IStorageBackend
{
    private static readonly StorageCapabilities SwiftCapabilities = new(true, true, true, true, true, true);
    private readonly HttpClient _client;
    private readonly SwiftConnectionConfig _configuration;
    private readonly SemaphoreSlim _authenticationGate = new(1, 1);
    private readonly string _prefix;
    private readonly bool _ownsClient;
    private readonly long _maxBufferedDownloadBytes;
    private string? _token;
    private string? _storageUrl;
    private DateTimeOffset _tokenExpiresAt;
    private int _disposed;

    public SwiftStorageBackend(
        string connectionId,
        HttpClient client,
        SwiftConnectionConfig configuration,
        bool ownsClient = false,
        long maxBufferedDownloadBytes = 67_108_864)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) throw new ArgumentException("Connection ID is required.", nameof(connectionId));
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(configuration);
        if (maxBufferedDownloadBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBufferedDownloadBytes));
        var normalized = StoragePath.Normalize(configuration.Prefix ?? string.Empty);
        if (normalized.IsFailure) throw new ArgumentException(normalized.Error!.Message, nameof(configuration));
        ConnectionId = connectionId;
        _client = client;
        _configuration = configuration;
        Root = normalized.Value!;
        _prefix = Root.Length == 0 ? string.Empty : Root + "/";
        _ownsClient = ownsClient;
        _maxBufferedDownloadBytes = maxBufferedDownloadBytes;
        if (configuration.AuthenticationMode == SwiftAuthenticationMode.StaticToken)
        {
            _token = configuration.Token;
            _storageUrl = configuration.StorageUrl?.TrimEnd('/');
            _tokenExpiresAt = DateTimeOffset.MaxValue;
        }
    }

    public string ConnectionId { get; }
    public StorageProvider Provider => StorageProvider.OpenStackSwift;
    public string Root { get; }
    public StorageCapabilities Capabilities => SwiftCapabilities;

    public async Task<Result<StorageItem>> GetInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(path);
        if (normalized.IsFailure) return Result<StorageItem>.Failure(normalized.Error!);
        if (normalized.Value!.Length == 0) return Result<StorageItem>.Success(DirectoryItem(string.Empty));
        try
        {
            using var response = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Head, ObjectUri(ToKey(normalized.Value))),
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return await GetDirectoryInfoAsync(normalized.Value, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Result<StorageItem>.Failure(FromStatus(response, "Get Swift object info"));
            return Result<StorageItem>.Success(ToItem(normalized.Value, response));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Get Swift object info")); }
    }

    public async Task<Result<bool>> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        var info = await GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
        if (info.IsSuccess) return Result<bool>.Success(true);
        return info.Error?.Code == StorageErrors.NotFoundCode
            ? Result<bool>.Success(false)
            : Result<bool>.Failure(info.Error!);
    }

    public async Task<Result<StoragePage>> ListAsync(string path, StorageListOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new StorageListOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<StoragePage>.Failure(validation.Error!);
        var normalized = Normalize(path);
        if (normalized.IsFailure) return Result<StoragePage>.Failure(normalized.Error!);
        try
        {
            var page = await ListProviderPageAsync(
                DirectoryPrefix(normalized.Value!),
                options.Recursive ? null : "/",
                options.PageSize,
                options.ContinuationToken,
                cancellationToken).ConfigureAwait(false);
            if (page.IsFailure) return Result<StoragePage>.Failure(page.Error!);
            var items = page.Value!.Items.Select(item =>
            {
                var providerPath = item.Subdirectory ?? item.Name!;
                var relative = FromKey(providerPath).TrimEnd('/');
                return relative.Length == 0
                    ? null
                    : item.Subdirectory is not null || providerPath.EndsWith('/')
                        ? DirectoryItem(relative)
                        : new StorageItem
                        {
                            Path = relative,
                            Name = NameOf(relative),
                            ItemType = StorageItemType.File,
                            Size = item.Bytes,
                            LastModified = item.LastModified,
                            ContentType = item.ContentType,
                            ETag = item.Hash
                        };
            }).Where(item => item is not null).Cast<StorageItem>().ToArray();
            return Result<StoragePage>.Success(new StoragePage(items, page.Value.NextMarker));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StoragePage>.Failure(Map(error, "List Swift objects")); }
    }

    public async Task<Result> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result.Failure(normalized.Error!);
        try
        {
            using var response = await SendAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Put, ObjectUri(ToKey(normalized.Value!.TrimEnd('/') + "/")));
                request.Content = new ByteArrayContent([]);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-directory");
                return request;
            }, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? Result.Success()
                : Result.Failure(FromStatus(response, "Create Swift directory"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Create Swift directory")); }
    }

    public async Task<Result<StorageItem>> UploadAsync(string path, Stream source, StorageUploadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new StorageUploadOptions();
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result<StorageItem>.Failure(normalized.Error!);
        var sourceStart = source.CanSeek ? source.Position : (long?)null;
        try
        {
            using var response = await SendAsync(() =>
            {
                if (sourceStart.HasValue) source.Position = sourceStart.Value;
                var request = new HttpRequestMessage(HttpMethod.Put, ObjectUri(ToKey(normalized.Value!)));
                request.Content = new StreamContent(new NonDisposingReadStream(source));
                if (!string.IsNullOrWhiteSpace(options.ContentType))
                    request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(options.ContentType);
                if (!options.Overwrite) request.Headers.TryAddWithoutValidation("If-None-Match", "*");
                foreach (var (name, value) in options.Metadata)
                    request.Headers.TryAddWithoutValidation("X-Object-Meta-" + name, value);
                return request;
            }, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Result<StorageItem>.Failure(FromStatus(response, "Upload Swift object"));
            return await GetInfoAsync(normalized.Value!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Upload Swift object")); }
    }

    public async Task<Result<StorageItem>> UploadBytesAsync(string path, byte[] content, StorageUploadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        await using var source = new MemoryStream(content, writable: false);
        return await UploadAsync(path, source, options, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<Stream>> DownloadAsync(string path, StorageDownloadOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new StorageDownloadOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<Stream>.Failure(validation.Error!);
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result<Stream>.Failure(normalized.Error!);
        HttpResponseMessage? response = null;
        try
        {
            response = await SendAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, ObjectUri(ToKey(normalized.Value!)));
                if (options.Offset > 0 || options.Length.HasValue)
                {
                    var end = options.Length.HasValue ? options.Offset + options.Length.Value - 1 : (long?)null;
                    request.Headers.Range = new RangeHeaderValue(options.Offset, end);
                }
                return request;
            }, cancellationToken, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var error = FromStatus(response, "Download Swift object");
                response.Dispose();
                response = null;
                return Result<Stream>.Failure(error);
            }
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var owned = new OwnedResourceStream(stream, response);
            response = null;
            return Result<Stream>.Success(owned);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<Stream>.Failure(Map(error, "Download Swift object")); }
        finally { response?.Dispose(); }
    }

    public async Task<Result<byte[]>> DownloadBytesAsync(string path, StorageDownloadOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new StorageDownloadOptions();
        var limit = options.MaxBufferedBytes ?? _maxBufferedDownloadBytes;
        var download = await DownloadAsync(path, options, cancellationToken).ConfigureAwait(false);
        if (download.IsFailure) return Result<byte[]>.Failure(download.Error!);
        await using var source = download.Value!;
        using var destination = new MemoryStream();
        var buffer = new byte[81_920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (destination.Length > Math.Min(limit, int.MaxValue) - read)
                return Result<byte[]>.Failure(StorageErrors.TooLarge($"The download exceeds the {limit} byte buffering limit."));
            destination.Write(buffer, 0, read);
        }
        return Result<byte[]>.Success(destination.ToArray());
    }

    public async Task<Result> DeleteAsync(string path, StorageDeleteOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new StorageDeleteOptions();
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result.Failure(normalized.Error!);
        try
        {
            var info = await GetInfoAsync(normalized.Value!, cancellationToken).ConfigureAwait(false);
            if (info.IsFailure) return options.IgnoreMissing && info.Error?.Code == StorageErrors.NotFoundCode
                ? Result.Success()
                : Result.Failure(info.Error!);
            if (info.Value!.ItemType == StorageItemType.Directory)
            {
                var names = await ListAllNamesAsync(DirectoryPrefix(normalized.Value!), cancellationToken).ConfigureAwait(false);
                if (!options.Recursive && names.Any(name => name != DirectoryPrefix(normalized.Value!)))
                    return Result.Failure(StorageErrors.Conflict("The Swift directory is not empty."));
                foreach (var name in names)
                {
                    var deleted = await DeleteObjectAsync(name, cancellationToken).ConfigureAwait(false);
                    if (deleted.IsFailure) return deleted;
                }
                var markerDelete = await DeleteObjectAsync(DirectoryPrefix(normalized.Value!), cancellationToken, ignoreMissing: true).ConfigureAwait(false);
                if (markerDelete.IsFailure) return markerDelete;
            }
            else
            {
                return await DeleteObjectAsync(ToKey(normalized.Value!), cancellationToken).ConfigureAwait(false);
            }
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Delete Swift object")); }
    }

    public async Task<Result> CopyAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new StorageTransferOptions();
        var source = NormalizeRequired(sourcePath);
        if (source.IsFailure) return Result.Failure(source.Error!);
        var destination = NormalizeRequired(destinationPath);
        if (destination.IsFailure) return Result.Failure(destination.Error!);
        try
        {
            var info = await GetInfoAsync(source.Value!, cancellationToken).ConfigureAwait(false);
            if (info.IsFailure) return Result.Failure(info.Error!);
            if (info.Value!.ItemType == StorageItemType.Directory)
            {
                var sourcePrefix = DirectoryPrefix(source.Value!);
                var destinationPrefix = DirectoryPrefix(destination.Value!);
                foreach (var name in await ListAllNamesAsync(sourcePrefix, cancellationToken).ConfigureAwait(false))
                {
                    var copied = await CopyObjectAsync(name, destinationPrefix + name[sourcePrefix.Length..], options.Overwrite, cancellationToken).ConfigureAwait(false);
                    if (copied.IsFailure) return copied;
                }
            }
            else
            {
                return await CopyObjectAsync(ToKey(source.Value!), ToKey(destination.Value!), options.Overwrite, cancellationToken).ConfigureAwait(false);
            }
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Copy Swift object")); }
    }

    public async Task<Result> MoveAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default)
    {
        var copied = await CopyAsync(sourcePath, destinationPath, options, cancellationToken).ConfigureAwait(false);
        if (copied.IsFailure) return copied;
        return await DeleteAsync(sourcePath, new StorageDeleteOptions { Recursive = true }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Head, ContainerUri()),
                cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? Result.Success()
                : Result.Failure(FromStatus(response, "Check Swift health"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Check Swift health")); }
    }

    public bool TryGetNativeClient<TClient>([NotNullWhen(true)] out TClient? client) where TClient : class
    {
        client = _client as TClient;
        return client is not null;
    }

    public Task<Result<NativeConnectionLease<TClient>>> OpenNativeConnectionAsync<TClient>(CancellationToken cancellationToken = default) where TClient : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_client is not TClient typed)
            return Task.FromResult(Result<NativeConnectionLease<TClient>>.Failure(StorageErrors.Unsupported($"Swift does not expose native type '{typeof(TClient).FullName}'.")));
        return Task.FromResult(Result<NativeConnectionLease<TClient>>.Success(new NativeConnectionLease<TClient>(typed, _ => ValueTask.CompletedTask)));
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _authenticationGate.Dispose();
            if (_ownsClient) _client.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead)
    {
        await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);
        var request = requestFactory();
        request.Headers.TryAddWithoutValidation("X-Auth-Token", _token);
        var response = await _client.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
        var canRetry = request.Content is null;
        request.Dispose();
        if (response.StatusCode != HttpStatusCode.Unauthorized ||
            _configuration.AuthenticationMode == SwiftAuthenticationMode.StaticToken)
            return response;

        InvalidateAuthentication();
        if (!canRetry)
            return response;
        response.Dispose();
        await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);
        request = requestFactory();
        request.Headers.TryAddWithoutValidation("X-Auth-Token", _token);
        response = await _client.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
        request.Dispose();
        return response;
    }

    private async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_token) && !string.IsNullOrWhiteSpace(_storageUrl) &&
            DateTimeOffset.UtcNow < _tokenExpiresAt - TimeSpan.FromMinutes(1))
            return;
        await _authenticationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrWhiteSpace(_token) && !string.IsNullOrWhiteSpace(_storageUrl) &&
                DateTimeOffset.UtcNow < _tokenExpiresAt - TimeSpan.FromMinutes(1))
                return;
            await AuthenticateKeystoneAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _authenticationGate.Release(); }
    }

    private async Task AuthenticateKeystoneAsync(CancellationToken cancellationToken)
    {
        var endpoint = KeystoneTokenUri(_configuration.AuthenticationUrl!);
        var payload = new
        {
            auth = new
            {
                identity = new
                {
                    methods = new[] { "password" },
                    password = new
                    {
                        user = new
                        {
                            name = _configuration.Username,
                            domain = new { name = _configuration.UserDomainName },
                            password = _configuration.Password
                        }
                    }
                },
                scope = new
                {
                    project = new
                    {
                        name = _configuration.ProjectName,
                        domain = new { name = _configuration.ProjectDomainName }
                    }
                }
            }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Keystone authentication failed with HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        if (!response.Headers.TryGetValues("X-Subject-Token", out var tokens))
            throw new InvalidOperationException("Keystone did not return X-Subject-Token.");
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var tokenNode = document.RootElement.GetProperty("token");
        var expires = tokenNode.TryGetProperty("expires_at", out var expiresNode) &&
            DateTimeOffset.TryParse(expiresNode.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : DateTimeOffset.UtcNow.AddHours(1);
        var storageUrl = _configuration.StorageUrl;
        if (string.IsNullOrWhiteSpace(storageUrl))
            storageUrl = FindObjectStoreEndpoint(tokenNode);
        _token = tokens.First();
        _storageUrl = storageUrl?.TrimEnd('/') ??
            throw new InvalidOperationException("Keystone catalog did not contain a matching object-store endpoint.");
        _tokenExpiresAt = expires;
    }

    private string? FindObjectStoreEndpoint(JsonElement tokenNode)
    {
        if (!tokenNode.TryGetProperty("catalog", out var catalog)) return null;
        foreach (var service in catalog.EnumerateArray())
        {
            if (!service.TryGetProperty("type", out var type) || type.GetString() != "object-store") continue;
            foreach (var endpoint in service.GetProperty("endpoints").EnumerateArray())
            {
                var endpointInterface = endpoint.TryGetProperty("interface", out var interfaceNode) ? interfaceNode.GetString() : null;
                var region = endpoint.TryGetProperty("region", out var regionNode) ? regionNode.GetString() : null;
                if (!string.Equals(endpointInterface, _configuration.EndpointInterface, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrWhiteSpace(_configuration.Region) &&
                    !string.Equals(region, _configuration.Region, StringComparison.OrdinalIgnoreCase)) continue;
                if (endpoint.TryGetProperty("url", out var url)) return url.GetString();
            }
        }
        return null;
    }

    private void InvalidateAuthentication()
    {
        _token = null;
        if (_configuration.AuthenticationMode != SwiftAuthenticationMode.StaticToken) _storageUrl = null;
        _tokenExpiresAt = default;
    }

    private async Task<Result<StorageItem>> GetDirectoryInfoAsync(string path, CancellationToken cancellationToken)
    {
        var page = await ListProviderPageAsync(DirectoryPrefix(path), null, 1, null, cancellationToken).ConfigureAwait(false);
        return page.IsFailure
            ? Result<StorageItem>.Failure(page.Error!)
            : page.Value!.Items.Count > 0
                ? Result<StorageItem>.Success(DirectoryItem(path))
                : Result<StorageItem>.Failure(StorageErrors.NotFound($"Swift item '{path}' was not found."));
    }

    private async Task<Result<SwiftPage>> ListProviderPageAsync(
        string prefix,
        string? delimiter,
        int limit,
        string? marker,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() =>
        {
            var parameters = new List<string>
            {
                "format=json",
                "prefix=" + Uri.EscapeDataString(prefix),
                "limit=" + limit.ToString(CultureInfo.InvariantCulture)
            };
            if (delimiter is not null) parameters.Add("delimiter=" + Uri.EscapeDataString(delimiter));
            if (!string.IsNullOrWhiteSpace(marker)) parameters.Add("marker=" + Uri.EscapeDataString(marker));
            return new HttpRequestMessage(HttpMethod.Get, ContainerUri() + "?" + string.Join("&", parameters));
        }, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return Result<SwiftPage>.Failure(FromStatus(response, "List Swift objects"));
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var items = new List<SwiftListItem>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.TryGetProperty("subdir", out var subdirectory))
            {
                items.Add(new SwiftListItem(null, subdirectory.GetString(), null, null, null, null));
                continue;
            }
            var name = element.GetProperty("name").GetString();
            long? bytes = element.TryGetProperty("bytes", out var bytesNode) ? bytesNode.GetInt64() : null;
            DateTimeOffset? lastModified = element.TryGetProperty("last_modified", out var modifiedNode) &&
                DateTimeOffset.TryParse(modifiedNode.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var modified)
                    ? modified
                    : null;
            items.Add(new SwiftListItem(
                name,
                null,
                bytes,
                lastModified,
                element.TryGetProperty("content_type", out var contentType) ? contentType.GetString() : null,
                element.TryGetProperty("hash", out var hash) ? hash.GetString() : null));
        }
        var last = items.LastOrDefault();
        var next = items.Count == limit ? last?.Subdirectory ?? last?.Name : null;
        return Result<SwiftPage>.Success(new SwiftPage(items, next));
    }

    private async Task<List<string>> ListAllNamesAsync(string prefix, CancellationToken cancellationToken)
    {
        var names = new List<string>();
        string? marker = null;
        do
        {
            var page = await ListProviderPageAsync(prefix, null, 1000, marker, cancellationToken).ConfigureAwait(false);
            if (page.IsFailure) throw new HttpRequestException(page.Error!.Message);
            names.AddRange(page.Value!.Items.Where(item => item.Name is not null).Select(item => item.Name!));
            marker = page.Value.NextMarker;
        } while (!string.IsNullOrWhiteSpace(marker));
        return names;
    }

    private async Task<Result> DeleteObjectAsync(string key, CancellationToken cancellationToken, bool ignoreMissing = false)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Delete, ObjectUri(key)),
            cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode || ignoreMissing && response.StatusCode == HttpStatusCode.NotFound)
            return Result.Success();
        return Result.Failure(FromStatus(response, "Delete Swift object"));
    }

    private async Task<Result> CopyObjectAsync(string sourceKey, string destinationKey, bool overwrite, CancellationToken cancellationToken)
    {
        if (!overwrite)
        {
            using var head = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Head, ObjectUri(destinationKey)),
                cancellationToken).ConfigureAwait(false);
            if (head.IsSuccessStatusCode) return Result.Failure(StorageErrors.Conflict("The Swift destination already exists."));
            if (head.StatusCode != HttpStatusCode.NotFound) return Result.Failure(FromStatus(head, "Check Swift copy destination"));
        }
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(new HttpMethod("COPY"), ObjectUri(sourceKey));
            request.Headers.TryAddWithoutValidation("Destination", "/" + Uri.EscapeDataString(_configuration.Container) + "/" + EncodeObjectPath(destinationKey));
            if (!overwrite) request.Headers.TryAddWithoutValidation("If-None-Match", "*");
            return request;
        }, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? Result.Success()
            : Result.Failure(FromStatus(response, "Copy Swift object"));
    }

    private Uri ContainerUri() => new($"{_storageUrl}/{Uri.EscapeDataString(_configuration.Container)}", UriKind.Absolute);
    private Uri ObjectUri(string key) => new($"{ContainerUri()}/{EncodeObjectPath(key)}", UriKind.Absolute);
    private static string EncodeObjectPath(string key) => string.Join("/", key.Split('/').Select(Uri.EscapeDataString));
    private static Uri KeystoneTokenUri(string authenticationUrl)
    {
        var value = authenticationUrl.TrimEnd('/');
        if (value.EndsWith("/auth/tokens", StringComparison.OrdinalIgnoreCase)) return new Uri(value);
        if (!value.EndsWith("/v3", StringComparison.OrdinalIgnoreCase)) value += "/v3";
        return new Uri(value + "/auth/tokens");
    }

    private string ToKey(string path) => _prefix + path;
    private string DirectoryPrefix(string path) => path.Length == 0 ? _prefix : ToKey(path).TrimEnd('/') + "/";
    private string FromKey(string key) => _prefix.Length == 0 ? key : key.StartsWith(_prefix, StringComparison.Ordinal) ? key[_prefix.Length..] : string.Empty;
    private Result<string> Normalize(string path) => StoragePath.Normalize(path);
    private Result<string> NormalizeRequired(string path)
    {
        var normalized = Normalize(path);
        return normalized.IsFailure || normalized.Value!.Length > 0
            ? normalized
            : Result<string>.Failure(StorageErrors.InvalidPath("A non-root storage path is required."));
    }

    private static StorageItem ToItem(string path, HttpResponseMessage response)
    {
        response.Content.Headers.TryGetValues("Content-Type", out var contentTypes);
        var metadata = response.Headers
            .Where(header => header.Key.StartsWith("X-Object-Meta-", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                header => header.Key["X-Object-Meta-".Length..],
                header => string.Join(",", header.Value),
                StringComparer.OrdinalIgnoreCase);
        return new StorageItem
        {
            Path = path,
            Name = NameOf(path),
            ItemType = path.EndsWith('/') ? StorageItemType.Directory : StorageItemType.File,
            Size = path.EndsWith('/') ? null : response.Content.Headers.ContentLength,
            LastModified = response.Content.Headers.LastModified,
            ContentType = contentTypes?.FirstOrDefault(),
            ETag = response.Headers.ETag?.Tag.Trim('"'),
            Metadata = metadata
        };
    }

    private static StorageItem DirectoryItem(string path) => new()
    {
        Path = path,
        Name = path.Length == 0 ? string.Empty : NameOf(path),
        ItemType = StorageItemType.Directory
    };

    private static string NameOf(string path) => path.Split('/')[^1];

    private static Error FromStatus(HttpResponseMessage response, string operation) => response.StatusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => StorageErrors.Unauthorized($"{operation}: access was denied."),
        HttpStatusCode.NotFound => StorageErrors.NotFound($"{operation}: item was not found."),
        HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => StorageErrors.Timeout($"{operation}: operation timed out."),
        HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed => StorageErrors.Conflict($"{operation}: Swift conflict."),
        HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError => StorageErrors.Unavailable($"{operation}: Swift service is unavailable."),
        _ => StorageErrors.ProviderError($"{operation}: Swift request failed with HTTP {(int)response.StatusCode}.")
    };

    private static Error Map(Exception exception, string operation)
    {
        if (exception is HttpRequestException request && request.StatusCode.HasValue)
            return request.StatusCode.Value switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => StorageErrors.Unauthorized($"{operation}: access was denied."),
                HttpStatusCode.NotFound => StorageErrors.NotFound($"{operation}: item was not found."),
                HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => StorageErrors.Timeout($"{operation}: operation timed out."),
                _ => StorageErrors.Unavailable($"{operation}: Swift service is unavailable.")
            };
        if (exception is TimeoutException or TaskCanceledException) return StorageErrors.Timeout($"{operation}: operation timed out.");
        if (exception is HttpRequestException) return StorageErrors.Unavailable($"{operation}: Swift service is unavailable.");
        return StorageErrors.ProviderError($"{operation}: Swift provider failed.", exception.Message);
    }

    private sealed record SwiftPage(IReadOnlyList<SwiftListItem> Items, string? NextMarker);
    private sealed record SwiftListItem(
        string? Name,
        string? Subdirectory,
        long? Bytes,
        DateTimeOffset? LastModified,
        string? ContentType,
        string? Hash);

    private sealed class NonDisposingReadStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { }
    }
}
