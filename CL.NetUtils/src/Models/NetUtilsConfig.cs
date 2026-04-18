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
    [ConfigField(Label = "Enabled", Description = "Master switch for NetUtils (DNSBL + GeoIP).", Group = "General", Order = 0)]
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
    [ConfigField(Label = "DNSBL Enabled", Group = "DNSBL", Order = 10)]
    public bool Enabled { get; set; } = true;

    /// <summary>Primary DNSBL services to query for IPv4 addresses.</summary>
    [ConfigField(Label = "IPv4 Services", InputType = ConfigInputType.Textarea,
        Description = "One DNSBL zone per line. Queried first for IPv4 addresses.",
        Group = "DNSBL", Order = 11)]
    public List<string> Ipv4Services { get; set; } = ["zen.spamhaus.org", "dnsbl.sorbs.net"];

    /// <summary>Fallback DNSBL services for IPv4 addresses.</summary>
    [ConfigField(Label = "IPv4 Fallback Services", InputType = ConfigInputType.Textarea,
        Description = "Consulted only when primary services don't return a match.",
        Group = "DNSBL", Order = 12, Collapsed = true)]
    public List<string> Ipv4FallbackServices { get; set; } = ["b.barracudacentral.org"];

    /// <summary>Primary DNSBL services to query for IPv6 addresses.</summary>
    [ConfigField(Label = "IPv6 Services", InputType = ConfigInputType.Textarea,
        Description = "One DNSBL zone per line. Queried first for IPv6 addresses.",
        Group = "DNSBL", Order = 13)]
    public List<string> Ipv6Services { get; set; } = ["zen.spamhaus.org", "dnsbl6.sorbs.net"];

    /// <summary>Fallback DNSBL services for IPv6 addresses.</summary>
    [ConfigField(Label = "IPv6 Fallback Services", InputType = ConfigInputType.Textarea,
        Group = "DNSBL", Order = 14, Collapsed = true)]
    public List<string> Ipv6FallbackServices { get; set; } = ["v6.b.barracudacentral.org"];

    /// <summary>DNS query timeout in seconds.</summary>
    [ConfigField(Label = "Timeout (s)", Min = 1, Max = 60, Group = "DNSBL", Order = 15)]
    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>Whether to run DNSBL queries in parallel.</summary>
    [ConfigField(Label = "Parallel Queries", Description = "Query all zones concurrently.",
        Group = "DNSBL", Order = 16, Collapsed = true)]
    public bool ParallelQueries { get; set; } = true;

    /// <summary>Whether to enable detailed per-service logging.</summary>
    [ConfigField(Label = "Detailed Logging", Description = "Log every zone lookup — noisy.",
        Group = "DNSBL", Order = 17, Collapsed = true)]
    public bool DetailedLogging { get; set; } = false;
}

/// <summary>
/// Configuration for IP geolocation using the MaxMind GeoIP2 database.
/// </summary>
public class GeoIpConfig
{
    /// <summary>Whether the GeoIP service is enabled.</summary>
    [ConfigField(Label = "GeoIP Enabled", Group = "GeoIP", Order = 20)]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Explicit path to the <c>.mmdb</c> database file.
    /// When empty the path is resolved automatically from the library data directory.
    /// </summary>
    [ConfigField(Label = "Database Path", Description = "Override the .mmdb path. Blank = use library data directory.",
        RequiresRestart = true, Group = "GeoIP", Order = 21)]
    public string DatabasePath { get; set; } = string.Empty;

    /// <summary>Whether to automatically download/update the database on startup.</summary>
    [ConfigField(Label = "Auto Update", Description = "Download latest GeoIP database on startup.",
        Group = "GeoIP", Order = 22)]
    public bool AutoUpdate { get; set; } = false;

    /// <summary>MaxMind account ID used for authenticated downloads.</summary>
    [ConfigField(Label = "MaxMind Account ID", Min = 0,
        Description = "Required for authenticated downloads.",
        Group = "GeoIP", Order = 23, Collapsed = true)]
    public int AccountId { get; set; } = 0;

    /// <summary>MaxMind license key used for authenticated downloads.</summary>
    [ConfigField(Label = "MaxMind License Key", InputType = ConfigInputType.Password, Secret = true,
        Description = "Required for authenticated GeoIP downloads.",
        Group = "GeoIP", Order = 24, Collapsed = true)]
    public string LicenseKey { get; set; } = string.Empty;

    /// <summary>URL used to download the GeoIP database archive.</summary>
    [ConfigField(Label = "Download URL", InputType = ConfigInputType.Url,
        Group = "GeoIP", Order = 25, Collapsed = true)]
    public string DownloadUrl { get; set; } =
        "https://download.maxmind.com/geoip/databases/GeoLite2-City/download?suffix=tar.gz";

    /// <summary>MaxMind database type (e.g. <c>GeoLite2-City</c>).</summary>
    [ConfigField(Label = "Database Type", Group = "GeoIP", Order = 26, Collapsed = true)]
    public string DatabaseType { get; set; } = "GeoLite2-City";

    /// <summary>HTTP download timeout in seconds.</summary>
    [ConfigField(Label = "Download Timeout (s)", Min = 1, Max = 600,
        Group = "GeoIP", Order = 27, Collapsed = true)]
    public int TimeoutSeconds { get; set; } = 30;
}
