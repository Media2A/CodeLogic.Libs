using CodeLogic.Core.Configuration;

namespace CL.NetUtils.Models;

/// <summary>
/// Main configuration model for the CL.NetUtils library.
/// Serialized as <c>config.netutils.json</c> in the library's config directory.
/// </summary>
[ConfigSection("netutils")]
public class NetUtilsConfig : ConfigModelBase
{
    /// <summary>Whether the NetUtils library is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>DNSBL blacklist-checking configuration.</summary>
    public DnsblConfig Dnsbl { get; set; } = new();

    /// <summary>IP geolocation (GeoIP) configuration.</summary>
    public GeoIpConfig GeoIp { get; set; } = new();

    /// <inheritdoc/>
    public override ConfigValidationResult Validate()
    {
        var errors = new List<string>();

        if (Dnsbl.TimeoutSeconds <= 0)
            errors.Add("DNSBL timeout must be > 0");

        return errors.Any()
            ? ConfigValidationResult.Invalid(errors)
            : ConfigValidationResult.Valid();
    }
}

/// <summary>
/// Configuration for DNS blacklist (DNSBL) checking.
/// </summary>
public class DnsblConfig
{
    /// <summary>Whether DNSBL checking is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Primary DNSBL services to query for IPv4 addresses.</summary>
    public List<string> Ipv4Services { get; set; } = ["zen.spamhaus.org", "dnsbl.sorbs.net"];

    /// <summary>Fallback DNSBL services for IPv4 addresses.</summary>
    public List<string> Ipv4FallbackServices { get; set; } = ["b.barracudacentral.org"];

    /// <summary>Primary DNSBL services to query for IPv6 addresses.</summary>
    public List<string> Ipv6Services { get; set; } = ["zen.spamhaus.org", "dnsbl6.sorbs.net"];

    /// <summary>Fallback DNSBL services for IPv6 addresses.</summary>
    public List<string> Ipv6FallbackServices { get; set; } = ["v6.b.barracudacentral.org"];

    /// <summary>DNS query timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>Whether to run DNSBL queries in parallel.</summary>
    public bool ParallelQueries { get; set; } = true;

    /// <summary>Whether to enable detailed per-service logging.</summary>
    public bool DetailedLogging { get; set; } = false;
}

/// <summary>
/// Configuration for IP geolocation using the MaxMind GeoIP2 database.
/// </summary>
public class GeoIpConfig
{
    /// <summary>Whether the GeoIP service is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Explicit path to the <c>.mmdb</c> database file.
    /// When empty the path is resolved automatically from the library data directory.
    /// </summary>
    public string DatabasePath { get; set; } = string.Empty;

    /// <summary>Whether to automatically download/update the database on startup.</summary>
    public bool AutoUpdate { get; set; } = false;

    /// <summary>MaxMind account ID used for authenticated downloads.</summary>
    public int AccountId { get; set; } = 0;

    /// <summary>MaxMind license key used for authenticated downloads.</summary>
    public string LicenseKey { get; set; } = string.Empty;

    /// <summary>URL used to download the GeoIP database archive.</summary>
    public string DownloadUrl { get; set; } =
        "https://download.maxmind.com/geoip/databases/GeoLite2-City/download?suffix=tar.gz";

    /// <summary>MaxMind database type (e.g. <c>GeoLite2-City</c>).</summary>
    public string DatabaseType { get; set; } = "GeoLite2-City";

    /// <summary>HTTP download timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}
