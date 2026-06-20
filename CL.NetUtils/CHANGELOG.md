# CL.NetUtils — Changelog

All notable changes to **CodeLogic.NetUtils** are documented here. Versions follow
[Semantic Versioning](https://semver.org/).

## 2026-06-20

### Documentation

- Full rewrite of the README and the deep docs page to house style: NuGet + MIT
  badges, blockquote tagline, concise quick-start, feature list, and a complete
  configuration table covering every `Dnsbl.*` and `GeoIp.*` field.
- Documented the correct load pattern (`Libraries.LoadAsync<NetUtilsLibrary>()`
  → `ConfigureAsync` → `StartAsync` → `Libraries.Get`) and both `Dnsbl` /
  `GeoIp` accessors, with the `HasGeoIp` guard for the optional GeoIP service.
- Documented both `CheckIpAsync` overloads, the default IPv4/IPv6 blacklist
  zones, and the per-call `DnsblCheckRequest` (per-call zone lists, timeout, and
  async allowlist predicate).
- Documented the `DnsblCheckResult` and `IpLocationResult` records field by
  field, including `IpLocationResult.IsSuccessful`.
- Documented GeoIP database resolution and auto-download: `DatabasePath` vs the
  `{dataDir}/geoip/{DatabaseType}.mmdb` fallback, and `DownloadDatabaseAsync`
  fetching/extracting the `.mmdb` via HTTP Basic Auth with `AccountId` +
  `LicenseKey`.

## [4.5.2] — 2026-06-20

### Documentation

- Corrected the README Quick Start to match the real API: `Dnsbl.CheckIpAsync`
  (was `CheckAsync`) returning `DnsblCheckResult.IsBlacklisted` / `MatchedService`
  (was `IsListed`), and the async `GeoIp.LookupIpAsync` returning
  `IpLocationResult.CountryName` / `CityName` (was a synchronous `Lookup`).
- Documented the `DnsblCheckRequest` overload of `CheckIpAsync` — per-call
  service lists plus an optional async allowlist predicate (`IsAllowedAsync`).
- Documented the `DnsblCheckResult` and `IpLocationResult` result records and
  the `IpLocationResult.IsSuccessful` helper.
- Documented that local/private addresses are short-circuited as not blacklisted.
- Added the `GeoIp.DownloadUrl` field to the sample `config.netutils.json`.

## [4.5.0] — 2026-05-24

### Changed

- **Unified versioning.** All CodeLogic.Libs now share a single version line
  controlled by `version.txt` in the repo root. This is a version alignment
  release — no functional changes to this library.
## [4.0.4] — 2026-04-16

### Changed

- README + manifest refresh for the v4 baseline. No functional changes vs 4.0.3.
- `LibraryManifest.Version` now reads from assembly metadata.

## [4.0.2] — 2026-04-09

### Added

- `DnsblCheckRequest` overload that accepts a caller-supplied DNSBL service
  list — useful when the calling app stores its own service registry instead
  of relying on the bundled defaults.

### Changed

- Annotated NetUtils configuration with `[ConfigField]` for the admin UI surface.
- Aligned with the v4 baseline across all libraries.

## [4.0.0] — 2026-04-09

Major rewrite. Republished as v4.0.0 to reset the version line under the
unified v4 baseline. GeoIP, DNSBL, and IP/CIDR utilities.

### Notes

- Earlier history is retained in the
  [git log](https://github.com/Media2A/CodeLogic.Libs/commits/main/CL.NetUtils).
