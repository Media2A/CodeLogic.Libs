namespace CL.NetUtils.Models;

/// <summary>
/// IP address protocol family.
/// </summary>
public enum IpAddressType
{
    /// <summary>IPv4 address (e.g. 192.168.1.1).</summary>
    IPv4,

    /// <summary>IPv6 address (e.g. 2001:db8::1).</summary>
    IPv6,

    /// <summary>Address type could not be determined.</summary>
    Unknown
}

/// <summary>
/// Result of a DNSBL blacklist check for a single IP address.
/// </summary>
public record DnsblCheckResult
{
    /// <summary>The IP address that was checked.</summary>
    public required string IpAddress { get; init; }

    /// <summary>Whether the IP address was found on at least one blacklist.</summary>
    public required bool IsBlacklisted { get; init; }

    /// <summary>The DNSBL service that matched, or <see langword="null"/> when not blacklisted.</summary>
    public string? MatchedService { get; init; }

    /// <summary>Protocol family of the checked address.</summary>
    public required IpAddressType AddressType { get; init; }

    /// <summary>Error message when the check itself failed; <see langword="null"/> on success.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>UTC timestamp of when the check was performed.</summary>
    public DateTime CheckedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Creates a result indicating the IP is blacklisted by the given service.</summary>
    public static DnsblCheckResult Blacklisted(string ip, IpAddressType type, string service) =>
        new() { IpAddress = ip, IsBlacklisted = true, MatchedService = service, AddressType = type };

    /// <summary>Creates a result indicating the IP passed all blacklist checks.</summary>
    public static DnsblCheckResult NotBlacklisted(string ip, IpAddressType type) =>
        new() { IpAddress = ip, IsBlacklisted = false, AddressType = type };

    /// <summary>Creates an error result when the check could not be completed.</summary>
    public static DnsblCheckResult Error(string ip, IpAddressType type, string error) =>
        new() { IpAddress = ip, IsBlacklisted = false, AddressType = type, ErrorMessage = error };
}

/// <summary>
/// Result of an IP geolocation lookup.
/// </summary>
public record IpLocationResult
{
    /// <summary>The IP address that was looked up.</summary>
    public required string IpAddress { get; init; }

    /// <summary>Full country name, or <see langword="null"/> when unavailable.</summary>
    public string? CountryName { get; init; }

    /// <summary>ISO 3166-1 alpha-2 country code, or <see langword="null"/> when unavailable.</summary>
    public string? CountryCode { get; init; }

    /// <summary>City name, or <see langword="null"/> when unavailable.</summary>
    public string? CityName { get; init; }

    /// <summary>State / province / subdivision name, or <see langword="null"/> when unavailable.</summary>
    public string? SubdivisionName { get; init; }

    /// <summary>Postal / ZIP code, or <see langword="null"/> when unavailable.</summary>
    public string? PostalCode { get; init; }

    /// <summary>Geographic latitude, or <see langword="null"/> when unavailable.</summary>
    public double? Latitude { get; init; }

    /// <summary>Geographic longitude, or <see langword="null"/> when unavailable.</summary>
    public double? Longitude { get; init; }

    /// <summary>IANA time zone identifier (e.g. <c>Europe/Copenhagen</c>), or <see langword="null"/>.</summary>
    public string? TimeZone { get; init; }

    /// <summary>ISP / organization name, or <see langword="null"/> when unavailable.</summary>
    public string? Isp { get; init; }

    /// <summary>Error message when the lookup failed; <see langword="null"/> on success.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// <see langword="true"/> when the lookup succeeded and country information is available.
    /// </summary>
    public bool IsSuccessful => string.IsNullOrEmpty(ErrorMessage) && !string.IsNullOrEmpty(CountryCode);

    /// <summary>UTC timestamp of when the lookup was performed.</summary>
    public DateTime LookedUpAt { get; init; } = DateTime.UtcNow;

    /// <summary>Creates a result indicating the IP was not found in the database.</summary>
    public static IpLocationResult NotFound(string ip, string? error = null) =>
        new() { IpAddress = ip, ErrorMessage = error ?? "Location not found" };

    /// <summary>Creates an error result when the lookup could not be completed.</summary>
    public static IpLocationResult Error(string ip, string error) =>
        new() { IpAddress = ip, ErrorMessage = error };
}
