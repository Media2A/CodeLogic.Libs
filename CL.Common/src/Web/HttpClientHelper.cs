using System.Text;
using System.Text.Json;
using CodeLogic.Core.Results;

namespace CL.Common.Web;

/// <summary>
/// Provides typed, async HTTP request helpers built on <see cref="HttpClient"/>.
/// Uses <see cref="Result{T}"/> for all operations so callers never need to catch exceptions.
/// Create one <see cref="HttpClient"/> instance per base URL and reuse it.
/// </summary>
public static class HttpClientHelper
{
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // ── GET ───────────────────────────────────────────────────────────────────

    /// <summary>Sends a GET request and returns the response body as a string.</summary>
    public static async Task<Result<string>> GetStringAsync(HttpClient client, string url,
        Dictionary<string, string>? headers = null)
    {
        try
        {
            using var request = BuildRequest(HttpMethod.Get, url, null, headers);
            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return Error.Internal(ErrorCode.Internal, $"HTTP {(int)response.StatusCode}: {body}");
            return body;
        }
        catch (Exception ex) { return Error.Internal(ErrorCode.ConnectionFailed, "GET request failed", ex.Message); }
    }

    /// <summary>Sends a GET request and deserializes the JSON response to <typeparamref name="T"/>.</summary>
    public static async Task<Result<T>> GetJsonAsync<T>(HttpClient client, string url,
        Dictionary<string, string>? headers = null)
    {
        var result = await GetStringAsync(client, url, headers);
        if (!result.IsSuccess) return result.Error!;
        try
        {
            var value = JsonSerializer.Deserialize<T>(result.Value!, _json);
            return value is null ? Error.Internal(ErrorCode.Internal, "Deserialization returned null") : value;
        }
        catch (Exception ex) { return Error.Internal(ErrorCode.Internal, "JSON deserialization failed", ex.Message); }
    }

    // ── POST ──────────────────────────────────────────────────────────────────

    /// <summary>Sends a POST request with a JSON body and returns the response string.</summary>
    public static async Task<Result<string>> PostJsonAsync<TBody>(HttpClient client, string url,
        TBody body, Dictionary<string, string>? headers = null)
    {
        try
        {
            var json    = JsonSerializer.Serialize(body, _json);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = BuildRequest(HttpMethod.Post, url, content, headers);
            using var response = await client.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return Error.Internal(ErrorCode.Internal, $"HTTP {(int)response.StatusCode}: {responseBody}");
            return responseBody;
        }
        catch (Exception ex) { return Error.Internal(ErrorCode.ConnectionFailed, "POST request failed", ex.Message); }
    }

    /// <summary>Sends a POST request and deserializes the JSON response.</summary>
    public static async Task<Result<TResponse>> PostJsonAsync<TBody, TResponse>(HttpClient client,
        string url, TBody body, Dictionary<string, string>? headers = null)
    {
        var result = await PostJsonAsync(client, url, body, headers);
        if (!result.IsSuccess) return result.Error!;
        try
        {
            var value = JsonSerializer.Deserialize<TResponse>(result.Value!, _json);
            return value is null ? Error.Internal(ErrorCode.Internal, "Deserialization returned null") : value;
        }
        catch (Exception ex) { return Error.Internal(ErrorCode.Internal, "JSON deserialization failed", ex.Message); }
    }

    // ── PUT ───────────────────────────────────────────────────────────────────

    /// <summary>Sends a PUT request with a JSON body.</summary>
    public static async Task<Result<string>> PutJsonAsync<TBody>(HttpClient client, string url,
        TBody body, Dictionary<string, string>? headers = null)
    {
        try
        {
            var json    = JsonSerializer.Serialize(body, _json);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = BuildRequest(HttpMethod.Put, url, content, headers);
            using var response = await client.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return Error.Internal(ErrorCode.Internal, $"HTTP {(int)response.StatusCode}: {responseBody}");
            return responseBody;
        }
        catch (Exception ex) { return Error.Internal(ErrorCode.ConnectionFailed, "PUT request failed", ex.Message); }
    }

    // ── DELETE ────────────────────────────────────────────────────────────────

    /// <summary>Sends a DELETE request.</summary>
    public static async Task<Result> DeleteAsync(HttpClient client, string url,
        Dictionary<string, string>? headers = null)
    {
        try
        {
            using var request = BuildRequest(HttpMethod.Delete, url, null, headers);
            using var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                return Error.Internal(ErrorCode.Internal, $"HTTP {(int)response.StatusCode}: {body}");
            }
            return Result.Success();
        }
        catch (Exception ex) { return Error.Internal(ErrorCode.ConnectionFailed, "DELETE request failed", ex.Message); }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static HttpRequestMessage BuildRequest(HttpMethod method, string url,
        HttpContent? content, Dictionary<string, string>? headers)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        if (headers != null)
            foreach (var (key, value) in headers)
                request.Headers.TryAddWithoutValidation(key, value);
        return request;
    }
}
