using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CL.SocialConnect.Events;
using CL.SocialConnect.Models;
using CL.SocialConnect.Models.Discord;
using CodeLogic.Core.Events;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;

namespace CL.SocialConnect.Services.Discord;

/// <summary>
/// Sends messages and embeds to Discord channels via the Webhook API.
/// </summary>
public sealed class DiscordWebhookService : IDisposable
{
    private readonly Models.DiscordConfig _config;
    private readonly ILogger? _logger;
    private readonly IEventBus? _events;
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Initializes a new instance of <see cref="DiscordWebhookService"/>.
    /// </summary>
    /// <param name="config">Discord configuration (webhook URL, timeout, defaults).</param>
    /// <param name="logger">Optional scoped logger.</param>
    /// <param name="events">Optional event bus for publishing <see cref="WebhookSentEvent"/>.</param>
    public DiscordWebhookService(Models.DiscordConfig config, ILogger? logger = null, IEventBus? events = null)
    {
        _config = config;
        _logger = logger;
        _events = events;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds > 0 ? config.TimeoutSeconds : 10)
        };
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Sends a plain text message to a Discord webhook.
    /// </summary>
    /// <param name="content">The text content (max 2000 characters).</param>
    /// <param name="webhookUrl">
    /// Webhook URL to post to. Falls back to <c>config.DefaultWebhookUrl</c> when not specified.
    /// </param>
    /// <returns>A <see cref="Result"/> indicating whether the message was delivered successfully.</returns>
    public Task<Result> SendMessageAsync(string content, string? webhookUrl = null)
    {
        var message = new DiscordWebhookMessage
        {
            Content = content,
            Username = string.IsNullOrWhiteSpace(_config.DefaultUsername) ? null : _config.DefaultUsername,
            AvatarUrl = string.IsNullOrWhiteSpace(_config.DefaultAvatarUrl) ? null : _config.DefaultAvatarUrl
        };
        return SendAsync(message, webhookUrl);
    }

    /// <summary>
    /// Sends a message with one or more rich embeds to a Discord webhook.
    /// </summary>
    /// <param name="embeds">Embed objects to attach (max 10).</param>
    /// <param name="content">Optional plain text content alongside the embeds.</param>
    /// <param name="webhookUrl">
    /// Webhook URL to post to. Falls back to <c>config.DefaultWebhookUrl</c> when not specified.
    /// </param>
    /// <returns>A <see cref="Result"/> indicating whether the message was delivered successfully.</returns>
    public Task<Result> SendEmbedAsync(IEnumerable<DiscordEmbed> embeds, string? content = null, string? webhookUrl = null)
    {
        var message = new DiscordWebhookMessage
        {
            Content = content,
            Embeds = embeds.ToList(),
            Username = string.IsNullOrWhiteSpace(_config.DefaultUsername) ? null : _config.DefaultUsername,
            AvatarUrl = string.IsNullOrWhiteSpace(_config.DefaultAvatarUrl) ? null : _config.DefaultAvatarUrl
        };
        return SendAsync(message, webhookUrl);
    }

    /// <summary>
    /// Sends a fully constructed <see cref="DiscordWebhookMessage"/> to a Discord webhook.
    /// </summary>
    /// <param name="message">The message payload to send.</param>
    /// <param name="webhookUrl">
    /// Webhook URL to post to. Falls back to <c>config.DefaultWebhookUrl</c> when not specified.
    /// </param>
    /// <returns>A <see cref="Result"/> indicating whether the message was delivered successfully.</returns>
    public async Task<Result> SendAsync(DiscordWebhookMessage message, string? webhookUrl = null)
    {
        var url = ResolveUrl(webhookUrl);
        if (string.IsNullOrWhiteSpace(url))
        {
            const string msg = "No webhook URL provided and no default configured";
            _logger?.Error(msg);
            return Result.Failure(Error.Validation("social.discord.no_url", msg));
        }

        var sentAt = DateTime.UtcNow;

        try
        {
            var json = JsonSerializer.Serialize(message, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            _logger?.Debug($"Sending webhook to {MaskUrl(url)}");

            var response = await _http.PostAsync(url, content).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                _logger?.Debug($"Webhook delivered successfully ({(int)response.StatusCode})");
                PublishWebhookEvent(url, true, sentAt, (int)response.StatusCode);
                return Result.Success();
            }

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var errMsg = $"Webhook delivery failed: HTTP {(int)response.StatusCode}. Body: {body}";
            _logger?.Warning(errMsg);
            PublishWebhookEvent(url, false, sentAt, (int)response.StatusCode, errMsg);

            return Result.Failure(
                Error.Internal(
                    "social.discord.delivery_failed",
                    $"[{SocialError.WebhookDeliveryFailed}] HTTP {(int)response.StatusCode}",
                    body
                )
            );
        }
        catch (TaskCanceledException ex)
        {
            var errMsg = $"[{SocialError.Timeout}] Webhook request timed out: {ex.Message}";
            _logger?.Error(errMsg, ex);
            PublishWebhookEvent(url, false, sentAt, null, errMsg);
            return Result.Failure(Error.Timeout("social.discord.timeout", errMsg));
        }
        catch (HttpRequestException ex)
        {
            var errMsg = $"[{SocialError.NetworkError}] Network error sending webhook: {ex.Message}";
            _logger?.Error(errMsg, ex);
            PublishWebhookEvent(url, false, sentAt, null, errMsg);
            return Result.Failure(Error.Unavailable("social.discord.network_error", errMsg));
        }
        catch (Exception ex)
        {
            var errMsg = $"[{SocialError.NetworkError}] Unexpected error sending webhook: {ex.Message}";
            _logger?.Error(errMsg, ex);
            PublishWebhookEvent(url, false, sentAt, null, errMsg);
            return Result.Failure(Error.Internal("social.discord.error", errMsg, ex.GetType().Name));
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string ResolveUrl(string? webhookUrl) =>
        !string.IsNullOrWhiteSpace(webhookUrl) ? webhookUrl : _config.DefaultWebhookUrl;

    private static string MaskUrl(string url)
    {
        // Mask the token portion of the webhook URL for safe logging
        var lastSlash = url.LastIndexOf('/');
        return lastSlash > 0 ? url[..(lastSlash + 1)] + "***" : url;
    }

    private void PublishWebhookEvent(string url, bool success, DateTime sentAt, int? statusCode, string? errorMessage = null)
    {
        _events?.Publish(new WebhookSentEvent(url, success, sentAt, statusCode, errorMessage));
    }

    public void Dispose() => _http.Dispose();
}
