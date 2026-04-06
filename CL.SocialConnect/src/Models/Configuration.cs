using CodeLogic.Core.Configuration;

namespace CL.SocialConnect.Models;

/// <summary>
/// Root configuration model for the CL.SocialConnect library.
/// Serialized as <c>config.socialconnect.json</c> in the library's config directory.
/// </summary>
[ConfigSection("socialconnect")]
public class SocialConnectConfig : ConfigModelBase
{
    /// <summary>Whether the SocialConnect library is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Discord integration configuration.</summary>
    public DiscordConfig Discord { get; set; } = new();

    /// <summary>Steam integration configuration.</summary>
    public SteamConfig Steam { get; set; } = new();

    /// <inheritdoc/>
    public override ConfigValidationResult Validate()
    {
        var errors = new List<string>();

        if (Steam.Enabled && string.IsNullOrWhiteSpace(Steam.ApiKey))
            errors.Add("Steam API key is required when Steam is enabled");

        if (Steam.AuthEnabled && string.IsNullOrWhiteSpace(Steam.AppId))
            errors.Add("Steam App ID is required when Steam authentication is enabled");

        if (Steam.CacheTtlSeconds <= 0)
            errors.Add("Steam cache TTL must be greater than 0");

        return errors.Any()
            ? ConfigValidationResult.Invalid(errors)
            : ConfigValidationResult.Valid();
    }
}

/// <summary>
/// Configuration for Discord webhook integration.
/// </summary>
public class DiscordConfig
{
    /// <summary>Whether Discord integration is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Default webhook URL used when no URL is specified in individual send calls.
    /// Format: <c>https://discord.com/api/webhooks/{id}/{token}</c>
    /// </summary>
    public string DefaultWebhookUrl { get; set; } = string.Empty;

    /// <summary>HTTP request timeout in seconds for webhook calls.</summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>Default username displayed for webhook messages (overrides Discord server setting).</summary>
    public string DefaultUsername { get; set; } = string.Empty;

    /// <summary>Default avatar URL for webhook messages.</summary>
    public string DefaultAvatarUrl { get; set; } = string.Empty;
}

/// <summary>
/// Configuration for Steam Web API integration.
/// </summary>
public class SteamConfig
{
    /// <summary>Whether Steam integration is enabled.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Steam Web API key. Required when <see cref="Enabled"/> is <c>true</c>.
    /// Obtain from <c>https://steamcommunity.com/dev/apikey</c>.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Whether Steam authentication (ticket validation) is enabled.</summary>
    public bool AuthEnabled { get; set; } = false;

    /// <summary>
    /// Steam App ID for authentication. Required when <see cref="AuthEnabled"/> is <c>true</c>.
    /// </summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>How long (in seconds) to cache Steam profile data. Default: 300 (5 minutes).</summary>
    public int CacheTtlSeconds { get; set; } = 300;

    /// <summary>HTTP request timeout in seconds for Steam API calls.</summary>
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>Base URL for the Steam Web API. Typically not changed.</summary>
    public string ApiBaseUrl { get; set; } = "https://api.steampowered.com";
}
