using System.Text.Json.Serialization;

namespace CL.SocialConnect.Models.Steam;

/// <summary>
/// Represents a game in a Steam player's library
/// returned by the <c>IPlayerService/GetOwnedGames</c> endpoint.
/// </summary>
public class SteamGame
{
    /// <summary>The game's Steam App ID.</summary>
    [JsonPropertyName("appid")]
    public int AppId { get; set; }

    /// <summary>The game's display name (requires <c>include_appinfo=true</c>).</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Total playtime in minutes.</summary>
    [JsonPropertyName("playtime_forever")]
    public int PlaytimeForever { get; set; }

    /// <summary>Playtime in the last two weeks, in minutes.</summary>
    [JsonPropertyName("playtime_2weeks")]
    public int? Playtime2Weeks { get; set; }

    /// <summary>URL segment for the game's icon image on the Steam CDN.</summary>
    [JsonPropertyName("img_icon_url")]
    public string? ImgIconUrl { get; set; }

    /// <summary>Unix timestamp of when the player last played the game.</summary>
    [JsonPropertyName("rtime_last_played")]
    public long? RtimeLastPlayed { get; set; }

    /// <summary>Total playtime as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan TotalPlaytime => TimeSpan.FromMinutes(PlaytimeForever);

    /// <summary>Playtime in the last two weeks as a <see cref="TimeSpan"/>, or <c>null</c>.</summary>
    public TimeSpan? RecentPlaytime =>
        Playtime2Weeks.HasValue ? TimeSpan.FromMinutes(Playtime2Weeks.Value) : null;

    /// <summary>The last played date/time in UTC, or <c>null</c> if never played.</summary>
    public DateTime? LastPlayed =>
        RtimeLastPlayed is > 0
            ? DateTimeOffset.FromUnixTimeSeconds(RtimeLastPlayed.Value).UtcDateTime
            : null;

    /// <summary>
    /// Builds the icon image URL for this game using the Steam CDN.
    /// Returns <c>null</c> when <see cref="ImgIconUrl"/> is not available.
    /// </summary>
    public string? GetIconUrl() =>
        string.IsNullOrEmpty(ImgIconUrl)
            ? null
            : $"https://media.steampowered.com/steamcommunity/public/images/apps/{AppId}/{ImgIconUrl}.jpg";

    /// <inheritdoc/>
    public override string ToString() =>
        Name is not null ? $"{Name} (AppID: {AppId})" : $"AppID: {AppId}";
}
