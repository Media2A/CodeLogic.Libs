# CL.TwoFactorAuth — Changelog

All notable changes to **CodeLogic.TwoFactorAuth** are documented here. Versions follow
[Semantic Versioning](https://semver.org/).

## [4.5.2] — 2026-06-20

### Documentation

- Corrected the README and the security guide to match the shipping API. The
  previous docs referenced members and config keys that do not exist
  (`GenerateSecret`, `Validate(secret, token)`, `GetOtpAuthUri`, backup-code
  helpers, and the `Issuer`/`SecretKeyLength`/`TokenValiditySeconds`/
  `AllowedClockSkewSeconds`/`QrCodeSize` settings).
- Documented the real configuration keys: `Enabled`, `TimeStepSeconds`,
  `WindowSize`, `QrCodeModuleSize`, and `ErrorCorrectionLevel`
  (`L`/`M`/`Q`/`H`).
- Documented the `Authenticator` and `QrCode` services and their full surface:
  `GenerateSecretKey`, `GenerateNewKey`, `GenerateTotpCode`, `ValidateTotp`
  (incl. the optional `userId` argument and the `TotpValidationResult` shape),
  `GetSecondsUntilExpiry`, `GetCurrentTimeWindow`, and the QR outputs
  `GenerateQrCodePng` / `GenerateQrCodeBmp` / `GenerateQrCodeBase64` /
  `GenerateQrCodeDataUri` / `SaveQrCodeToFile`.
- Documented the `TwoFactorKey.ProvisioningUri` (`otpauth://`) property, the
  `SecretKeyGeneratedEvent` / `TotpValidatedEvent` bus events, and the actual
  health-check behavior (live TOTP key generation).

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

### Changed

- Annotated 2FA configuration with `[ConfigField]` for the admin UI surface.
- Aligned with the v4 baseline across all libraries.

## [4.0.0] — 2026-04-09

Major rewrite. Republished as v4.0.0 to reset the version line under the
unified v4 baseline. TOTP-based 2FA with QR code generation, backup codes,
and a CodeLogic-native flow for Google Authenticator / Authy / 1Password.

### Notes

- Earlier history is retained in the
  [git log](https://github.com/Media2A/CodeLogic.Libs/commits/main/CL.TwoFactorAuth).
