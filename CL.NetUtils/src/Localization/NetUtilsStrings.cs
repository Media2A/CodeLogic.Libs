using CodeLogic.Core.Localization;

namespace CL.NetUtils.Localization;

/// <summary>
/// Localized string resources for the CL.NetUtils library.
/// </summary>
[LocalizationSection("netutils")]
public class NetUtilsStrings : LocalizationModelBase
{
    [LocalizedString(Description = "Library initialized")]
    public string LibraryInitialized { get; set; } = "NetUtils library initialized";

    [LocalizedString(Description = "Library started")]
    public string LibraryStarted { get; set; } = "NetUtils library started";

    [LocalizedString(Description = "Library stopped")]
    public string LibraryStopped { get; set; } = "NetUtils library stopped";

    [LocalizedString(Description = "DNSBL check result: blacklisted")]
    public string IpBlacklisted { get; set; } = "IP {0} is blacklisted by {1}";

    [LocalizedString(Description = "DNSBL check result: clean")]
    public string IpClean { get; set; } = "IP {0} is not blacklisted";

    [LocalizedString(Description = "GeoIP database not found")]
    public string GeoIpDatabaseNotFound { get; set; } = "GeoIP database not found, downloading...";

    [LocalizedString(Description = "GeoIP initialized")]
    public string GeoIpInitialized { get; set; } = "GeoIP service initialized with database: {0}";

    [LocalizedString(Description = "GeoIP unavailable")]
    public string GeoIpUnavailable { get; set; } = "GeoIP service unavailable: {0}";

    [LocalizedString(Description = "Invalid IP address")]
    public string InvalidIpAddress { get; set; } = "Invalid IP address: {0}";
}
