# CL.NetUtils

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.NetUtils)](https://www.nuget.org/packages/CodeLogic.NetUtils)

DNSBL blacklist checking and IP geolocation services for CodeLogic 3 applications, using MaxMind GeoIP2.

## Install

```
dotnet add package CodeLogic.NetUtils
```

## Quick Start

```csharp
var netUtils = new NetUtilsLibrary();
// After library initialization via CodeLogic framework:

// Check if an IP is blacklisted
var result = await netUtils.Dnsbl.CheckAsync("203.0.113.1");
Console.WriteLine($"Listed: {result.IsListed}");

// Look up IP geolocation
if (netUtils.HasGeoIp)
{
    var geo = netUtils.GeoIp.Lookup("203.0.113.1");
    Console.WriteLine($"Country: {geo.Country}, City: {geo.City}");
}
```

## Features

- **DNSBL checking** — query multiple blacklist services (IPv4 and IPv6) with parallel lookups
- **IP geolocation** — city-level lookups via MaxMind GeoLite2/GeoIP2 databases
- **Auto-update** — optionally download and refresh the GeoIP database on startup
- **Configurable services** — primary and fallback DNSBL providers per address family

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
    "DatabaseType": "GeoLite2-City",
    "TimeoutSeconds": 30
  }
}
```

## Documentation

Full API docs: [https://github.com/Media2A/CodeLogic.Libs](https://github.com/Media2A/CodeLogic.Libs)

## Requirements

- .NET 10.0+
- CodeLogic 3.0.0+
- MaxMind.GeoIP2 5.x
- A MaxMind GeoLite2 or GeoIP2 database file (`.mmdb`)

## License

MIT -- see [LICENSE](../LICENSE) for details.
