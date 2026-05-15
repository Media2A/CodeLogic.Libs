# CL.SocialConnect — Changelog

All notable changes to **CodeLogic.SocialConnect** are documented here. Versions follow
[Semantic Versioning](https://semver.org/).

## [4.0.4] — 2026-04-16

### Changed

- README + manifest refresh for the v4 baseline. No functional changes vs 4.0.3.
- `LibraryManifest.Version` now reads from assembly metadata.

## [4.0.2] — 2026-04-09

### Changed

- Annotated SocialConnect configuration with `[ConfigField]` for the admin UI surface.
- Aligned with the v4 baseline across all libraries.

### Fixed

- Closed resource leaks in the OAuth client + reworked the health check so
  it no longer mutates internal state as a side-effect.

## [4.0.0] — 2026-04-09

Major rewrite. Republished as v4.0.0 to reset the version line under the
unified v4 baseline. OAuth/OIDC providers (Google, Discord, Steam,
generic OIDC) with a uniform abstraction.

### Notes

- Earlier history is retained in the
  [git log](https://github.com/Media2A/CodeLogic.Libs/commits/main/CL.SocialConnect).
