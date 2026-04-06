using System.Text.Json;
using CL.SocialConnect.Events;
using CL.SocialConnect.Models;
using CL.SocialConnect.Models.Steam;
using CodeLogic.Core.Events;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;

namespace CL.SocialConnect.Services.Steam;

/// <summary>
/// Validates Steam authentication tickets via the Steam Web API
/// (<c>ISteamUserAuth/AuthenticateUserTicket</c>).
/// Use this to verify that a client's Steam session is genuine before granting access.
/// </summary>
public sealed class SteamAuthenticationService : IDisposable
{
    private readonly Models.SteamConfig _config;
    private readonly ILogger? _logger;
    private readonly IEventBus? _events;
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Initializes a new instance of <see cref="SteamAuthenticationService"/>.
    /// </summary>
    /// <param name="config">Steam configuration (API key, App ID, timeouts).</param>
    /// <param name="logger">Optional scoped logger.</param>
    /// <param name="events">Optional event bus for publishing <see cref="SteamAuthenticatedEvent"/>.</param>
    public SteamAuthenticationService(Models.SteamConfig config, ILogger? logger = null, IEventBus? events = null)
    {
        _config = config;
        _logger = logger;
        _events = events;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds > 0 ? config.TimeoutSeconds : 15),
            BaseAddress = new Uri(config.ApiBaseUrl.TrimEnd('/') + "/")
        };
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates a Steam session ticket against the Steam Web API.
    /// The ticket should be obtained from the Steam client via <c>ISteamUser::GetAuthSessionTicket</c>.
    /// </summary>
    /// <param name="ticket">The hex-encoded session ticket string from the Steam client.</param>
    /// <param name="appId">
    /// The App ID to validate the ticket against. Falls back to <c>config.AppId</c> when not specified.
    /// </param>
    /// <returns>
    /// A <see cref="Result{T}"/> containing a <see cref="SteamAuthResult"/> on success.
    /// </returns>
    public async Task<Result<SteamAuthResult>> AuthenticateAsync(string ticket, string? appId = null)
    {
        if (string.IsNullOrWhiteSpace(ticket))
        {
            const string msg = "Ticket cannot be empty";
            return Result<SteamAuthResult>.Failure(
                Error.Validation("social.steam.invalid_ticket", $"[{SocialError.AuthenticationFailed}] {msg}")
            );
        }

        var resolvedAppId = !string.IsNullOrWhiteSpace(appId) ? appId : _config.AppId;
        if (string.IsNullOrWhiteSpace(resolvedAppId))
        {
            const string msg = "App ID is required for Steam authentication";
            return Result<SteamAuthResult>.Failure(
                Error.Validation("social.steam.no_appid", $"[{SocialError.ConfigurationError}] {msg}")
            );
        }

        var authenticatedAt = DateTime.UtcNow;

        try
        {
            var url = $"ISteamUserAuth/AuthenticateUserTicket/v1/?key={_config.ApiKey}&appid={resolvedAppId}&ticket={ticket}";
            _logger?.Debug($"Validating Steam ticket for App ID {resolvedAppId}");

            var json = await _http.GetStringAsync(url).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<SteamAuthApiResponse>(json, JsonOptions);

            // Check for API-level error
            if (response?.Response?.Error is { } error)
            {
                var errMsg = $"[{SocialError.AuthenticationFailed}] Steam auth error {error.ErrorCode}: {error.ErrorDesc}";
                _logger?.Warning(errMsg);

                _events?.Publish(new SteamAuthenticatedEvent(
                    string.Empty, false, authenticatedAt, resolvedAppId, errMsg
                ));

                return Result<SteamAuthResult>.Failure(
                    Error.Unauthorized("social.steam.auth_failed", errMsg, error.ErrorCode.ToString())
                );
            }

            var @params = response?.Response?.Params;
            if (@params is null || @params.Result != "OK")
            {
                var resultStr = @params?.Result ?? "null";
                var errMsg = $"[{SocialError.AuthenticationFailed}] Steam auth result was not OK: {resultStr}";
                _logger?.Warning(errMsg);

                _events?.Publish(new SteamAuthenticatedEvent(
                    string.Empty, false, authenticatedAt, resolvedAppId, errMsg
                ));

                return Result<SteamAuthResult>.Failure(
                    Error.Unauthorized("social.steam.auth_failed", errMsg)
                );
            }

            var authResult = new SteamAuthResult
            {
                SteamId = @params.SteamId,
                IsAuthenticated = true,
                OwnerSteamId = @params.OwnerSteamId,
                VacBanned = @params.VacBanned ? 1 : 0,
                PublisherBanned = @params.PublisherBanned ? 1 : 0
            };

            _logger?.Info($"Steam authentication succeeded for {authResult.SteamId}");

            _events?.Publish(new SteamAuthenticatedEvent(
                authResult.SteamId, true, authenticatedAt, resolvedAppId
            ));

            return Result<SteamAuthResult>.Success(authResult);
        }
        catch (TaskCanceledException ex)
        {
            var errMsg = $"[{SocialError.Timeout}] Steam auth request timed out: {ex.Message}";
            _logger?.Error(errMsg, ex);

            _events?.Publish(new SteamAuthenticatedEvent(
                string.Empty, false, authenticatedAt, resolvedAppId, errMsg
            ));

            return Result<SteamAuthResult>.Failure(Error.Timeout("social.steam.timeout", errMsg));
        }
        catch (HttpRequestException ex)
        {
            var errMsg = $"[{SocialError.NetworkError}] Network error during Steam auth: {ex.Message}";
            _logger?.Error(errMsg, ex);

            _events?.Publish(new SteamAuthenticatedEvent(
                string.Empty, false, authenticatedAt, resolvedAppId, errMsg
            ));

            return Result<SteamAuthResult>.Failure(Error.Unavailable("social.steam.network_error", errMsg));
        }
        catch (JsonException ex)
        {
            var errMsg = $"[{SocialError.SerializationError}] Failed to parse Steam auth response: {ex.Message}";
            _logger?.Error(errMsg, ex);

            _events?.Publish(new SteamAuthenticatedEvent(
                string.Empty, false, authenticatedAt, resolvedAppId, errMsg
            ));

            return Result<SteamAuthResult>.Failure(Error.Internal("social.steam.parse_error", errMsg));
        }
        catch (Exception ex)
        {
            var errMsg = $"[{SocialError.NetworkError}] Unexpected error during Steam auth: {ex.Message}";
            _logger?.Error(errMsg, ex);

            _events?.Publish(new SteamAuthenticatedEvent(
                string.Empty, false, authenticatedAt, resolvedAppId, errMsg
            ));

            return Result<SteamAuthResult>.Failure(Error.Internal("social.steam.error", errMsg, ex.GetType().Name));
        }
    }
    public void Dispose() => _http.Dispose();
}
