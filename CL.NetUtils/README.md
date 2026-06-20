# CodeLogic.NetUtils

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.NetUtils)](https://www.nuget.org/packages/CodeLogic.NetUtils)

DNSBL blacklist checking and IP geolocation services for [CodeLogic](https://github.com/Media2A/CodeLogic) applications, using MaxMind GeoIP2.

## Install

```
dotnet add package CodeLogic.NetUtils
```

## Quick Start

```csharp
var netUtils = new NetUtilsLibrary();
// After library initialization via CodeLogic framework:

// Check if an IP is blacklisted (uses the services from config.netutils.json)
DnsblCheckResult result = await netUtils.Dnsbl.CheckIpAsync("203.0.113.1");
if (result.IsBlacklisted)
    Console.WriteLine($"Listed by {result.MatchedService}");

// Look up IP geolocation — guard with HasGeoIp (the service is optional)
if (netUtils.HasGeoIp)
{
    IpLocationResult geo = await netUtils.GeoIp.LookupIpAsync("203.0.113.1");
    if (geo.IsSuccessful)
        Console.WriteLine($"Country: {geo.CountryName}, City: {geo.CityName}");
}
```

Local and private addresses (loopback, RFC 1918, IPv6 link/site-local and
unique-local) are short-circuited by the DNSBL checker and returned as
**not blacklisted** without any DNS query.

## Features

- **DNSBL checking** — query multiple blacklist services (IPv4 and IPv6) with parallel lookups
- **IP geolocation** — city-level lookups via MaxMind GeoLite2/GeoIP2 databases
- **Auto-update** — optionally download and refresh the GeoIP database on startup
- **Configurable services** — primary and fallback DNSBL providers per address family

## Custom service lists & allowlists

Apps that store their own DNSBL zones or IP allowlists (e.g. in a database)
can bypass the static library config with the `DnsblCheckRequest` overload of
`CheckIpAsync`. Supply per-call service lists and an optional async allowlist
predicate — returning `true` from the predicate skips all DNSBL queries for
that IP and returns *not blacklisted*.

```csharp
var request = new DnsblCheckRequest
{
    Ipv4Services         = ["zen.spamhaus.org"],
    Ipv4FallbackServices = ["b.barracudacentral.org"],
    Ipv6Services         = ["zen.spamhaus.org"],
    Timeout              = TimeSpan.FromSeconds(5),
    IsAllowedAsync       = async (ip, ct) => await myAllowlist.ContainsAsync(ip, ct)
};

DnsblCheckResult result = await netUtils.Dnsbl.CheckIpAsync("203.0.113.1", request);
```

## Result models

`CheckIpAsync` returns a `DnsblCheckResult` record:

| Member | Description |
| --- | --- |
| `IpAddress` | The address that was checked. |
| `IsBlacklisted` | `true` when found on at least one list. |
| `MatchedService` | The DNSBL zone that matched, or `null`. |
| `AddressType` | `IpAddressType.IPv4` / `IPv6` / `Unknown`. |
| `ErrorMessage` | Non-`null` when the check itself failed. |
| `CheckedAt` | UTC timestamp. |

`LookupIpAsync` returns an `IpLocationResult` record with `CountryName`,
`CountryCode`, `CityName`, `SubdivisionName`, `PostalCode`, `Latitude`,
`Longitude`, `TimeZone`, `Isp`, `ErrorMessage`, and `LookedUpAt`. Use the
`IsSuccessful` property (true when there is no error and a country code is
present) to test the outcome.

## Configuration

Config file: `config.netutils.json`

```json
{
  "Enabled": true,
  "Dnsbl": {
    "Enabled": true,
    "Ipv4Services": ["zen.spamhaus.org", "dnsbl.sorbs.net"],
    "Ipv4FallbackServices": ["b.barracudacentral.org"],
    "Ipv6Services": ["zen.spamhaus.org", "dnsbl6.sorbs.net"],
    "Ipv6FallbackServices": ["v6.b.barracudacentral.org"],
    "TimeoutSeconds": 5,
    "ParallelQueries": true,
    "DetailedLogging": false
  },
  "GeoIp": {
    "Enabled": true,
    "DatabasePath": "",
    "AutoUpdate": false,
    "AccountId": 0,
    "LicenseKey": "",
    "DownloadUrl": "https://download.maxmind.com/geoip/databases/GeoLite2-City/download?suffix=tar.gz",
    "DatabaseType": "GeoLite2-City",
    "TimeoutSeconds": 30
  }
}
```

## Documentation

Full API docs: [https://github.com/Media2A/CodeLogic.Libs](https://github.com/Media2A/CodeLogic.Libs)

## Requirements

- .NET 10.0+
- [CodeLogic 3.x or 4.x](https://github.com/Media2A/CodeLogic)
- MaxMind.GeoIP2 5.x
- A MaxMind GeoLite2 or GeoIP2 database file (`.mmdb`)

## License

MIT — see [LICENSE](https://github.com/Media2A/CodeLogic.Libs/blob/main/LICENSE)
