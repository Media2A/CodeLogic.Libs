using System.Text.Json.Serialization;

namespace CL.SocialConnect.Models.Steam;

/// <summary>
/// Represents the VAC and game-ban status of a Steam player
/// returned by the <c>ISteamUser/GetPlayerBans</c> endpoint.
/// </summary>
public class SteamPlayerBans
{
    /// <summary>The player's 64-bit Steam ID.</summary>
    [JsonPropertyName("SteamId")]
    public string SteamId { get; set; } = string.Empty;

    /// <summary>Whether the player is banned from the Steam Community.</summary>
    [JsonPropertyName("CommunityBanned")]
    public bool CommunityBanned { get; set; }

    /// <summary>Whether the player has one or more VAC bans on record.</summary>
    [JsonPropertyName("VACBanned")]
    public bool VacBanned { get; set; }

    /// <summary>Number of VAC bans on record.</summary>
    [JsonPropertyName("NumberOfVACBans")]
    public int NumberOfVacBans { get; set; }

    /// <summary>Number of days since the most recent ban.</summary>
    [JsonPropertyName("DaysSinceLastBan")]
    public int DaysSinceLastBan { get; set; }

    /// <summary>Number of game bans on record.</summary>
    [JsonPropertyName("NumberOfGameBans")]
    public int NumberOfGameBans { get; set; }

    /// <summary>
    /// The player's economy ban status: <c>"none"</c>, <c>"probation"</c>, or <c>"banned"</c>.
    /// </summary>
    [JsonPropertyName("EconomyBan")]
    public string EconomyBan { get; set; } = "none";

    /// <summary>Returns <c>true</c> when the player has any type of ban on record.</summary>
    public bool HasAnyBan =>
        CommunityBanned || VacBanned || NumberOfGameBans > 0 || EconomyBan != "none";

    /// <inheritdoc/>
    public override string ToString() =>
        HasAnyBan
            ? $"{SteamId}: VAC={VacBanned}, GameBans={NumberOfGameBans}, Economy={EconomyBan}"
            : $"{SteamId}: No bans";
}
