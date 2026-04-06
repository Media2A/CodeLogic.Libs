using System.Text.Json.Serialization;

namespace CL.SocialConnect.Models.Steam;

/// <summary>
/// Represents the result of a Steam authentication ticket validation
/// returned by <c>ISteamUserAuth/AuthenticateUserTicket</c>.
/// </summary>
public class SteamAuthResult
{
    /// <summary>The 64-bit Steam ID of the authenticated user.</summary>
    public string SteamId { get; set; } = string.Empty;

    /// <summary>Whether authentication succeeded.</summary>
    public bool IsAuthenticated { get; set; }

    /// <summary>The app the ticket was issued for (should match your App ID).</summary>
    public string? OwnerSteamId { get; set; }

    /// <summary>Optional error description when <see cref="IsAuthenticated"/> is <c>false</c>.</summary>
    public string? ErrorDescription { get; set; }

    /// <summary>Vacation Account Flags from the Steam API response.</summary>
    public int VacBanned { get; set; }

    /// <summary>Publisher VAC ban status.</summary>
    public int PublisherBanned { get; set; }

    /// <inheritdoc/>
    public override string ToString() =>
        IsAuthenticated
            ? $"Authenticated: {SteamId}"
            : $"Auth failed: {ErrorDescription ?? "Unknown error"}";
}

/// <summary>
/// Internal response wrapper for the ISteamUserAuth/AuthenticateUserTicket endpoint.
/// </summary>
internal class SteamAuthApiResponse
{
    [JsonPropertyName("response")]
    public SteamAuthApiParams? Response { get; set; }
}

internal class SteamAuthApiParams
{
    [JsonPropertyName("params")]
    public SteamAuthApiParamsData? Params { get; set; }

    [JsonPropertyName("error")]
    public SteamAuthApiError? Error { get; set; }
}

internal class SteamAuthApiParamsData
{
    [JsonPropertyName("result")]
    public string Result { get; set; } = string.Empty;

    [JsonPropertyName("steamid")]
    public string SteamId { get; set; } = string.Empty;

    [JsonPropertyName("ownersteamid")]
    public string OwnerSteamId { get; set; } = string.Empty;

    [JsonPropertyName("vacbanned")]
    public bool VacBanned { get; set; }

    [JsonPropertyName("publisherbanned")]
    public bool PublisherBanned { get; set; }
}

internal class SteamAuthApiError
{
    [JsonPropertyName("errorcode")]
    public int ErrorCode { get; set; }

    [JsonPropertyName("errordesc")]
    public string ErrorDesc { get; set; } = string.Empty;
}
