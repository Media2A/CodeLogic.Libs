using System.Text.Json.Serialization;

namespace CL.SocialConnect.Models.Steam;

/// <summary>
/// Represents a Steam player's public profile summary
/// returned by the <c>ISteamUser/GetPlayerSummaries</c> endpoint.
/// </summary>
public class SteamPlayer
{
    /// <summary>The player's 64-bit Steam ID.</summary>
    [JsonPropertyName("steamid")]
    public string SteamId { get; set; } = string.Empty;

    /// <summary>
    /// Community visibility state:
    /// 1 = Private, 2 = Friends only, 3 = Friends of friends, 4 = Users only, 5 = Public.
    /// </summary>
    [JsonPropertyName("communityvisibilitystate")]
    public int CommunityVisibilityState { get; set; }

    /// <summary>Whether the profile info is configured (1 = configured).</summary>
    [JsonPropertyName("profilestate")]
    public int ProfileState { get; set; }

    /// <summary>The player's display name on Steam.</summary>
    [JsonPropertyName("personaname")]
    public string PersonaName { get; set; } = string.Empty;

    /// <summary>URL to the player's Steam profile page.</summary>
    [JsonPropertyName("profileurl")]
    public string ProfileUrl { get; set; } = string.Empty;

    /// <summary>URL to the player's 32×32 avatar image.</summary>
    [JsonPropertyName("avatar")]
    public string Avatar { get; set; } = string.Empty;

    /// <summary>URL to the player's 64×64 avatar image.</summary>
    [JsonPropertyName("avatarmedium")]
    public string AvatarMedium { get; set; } = string.Empty;

    /// <summary>URL to the player's 184×184 avatar image.</summary>
    [JsonPropertyName("avatarfull")]
    public string AvatarFull { get; set; } = string.Empty;

    /// <summary>
    /// Persona state (online status):
    /// 0 = Offline, 1 = Online, 2 = Busy, 3 = Away, 4 = Snooze, 5 = Looking to trade, 6 = Looking to play.
    /// </summary>
    [JsonPropertyName("personastate")]
    public int PersonaState { get; set; }

    /// <summary>The player's real name (if public and set).</summary>
    [JsonPropertyName("realname")]
    public string? RealName { get; set; }

    /// <summary>The player's primary Steam group ID (64-bit).</summary>
    [JsonPropertyName("primaryclanid")]
    public string? PrimaryClanId { get; set; }

    /// <summary>Unix timestamp of when the account was created.</summary>
    [JsonPropertyName("timecreated")]
    public long? TimeCreated { get; set; }

    /// <summary>The game ID the player is currently playing.</summary>
    [JsonPropertyName("gameid")]
    public string? GameId { get; set; }

    /// <summary>The name of the game the player is currently playing.</summary>
    [JsonPropertyName("gameextrainfo")]
    public string? GameExtraInfo { get; set; }

    /// <summary>ISO 3166-1 alpha-2 country code (if public).</summary>
    [JsonPropertyName("loccountrycode")]
    public string? LocCountryCode { get; set; }

    /// <summary>State code within the country (if public).</summary>
    [JsonPropertyName("locstatecode")]
    public string? LocStateCode { get; set; }

    /// <summary>
    /// Returns <c>true</c> when the player's profile is publicly visible.
    /// </summary>
    public bool IsPublic => CommunityVisibilityState == 3;

    /// <summary>
    /// Returns <c>true</c> when the player is currently in a game.
    /// </summary>
    public bool IsInGame => !string.IsNullOrEmpty(GameId);

    /// <summary>
    /// Returns the account creation date, or <c>null</c> if not available.
    /// </summary>
    public DateTime? AccountCreated =>
        TimeCreated.HasValue
            ? DateTimeOffset.FromUnixTimeSeconds(TimeCreated.Value).UtcDateTime
            : null;

    /// <inheritdoc/>
    public override string ToString() => $"{PersonaName} ({SteamId})";
}
