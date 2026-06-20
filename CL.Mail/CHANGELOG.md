# CL.Mail — Changelog

All notable changes to **CodeLogic.Mail** are documented here. Versions follow
[Semantic Versioning](https://semver.org/).

## [4.5.1] — 2026-06-20

### Documentation

- Rewrote the README and the Mail & Templates guide to match the current public
  API. The previous docs described members that no longer exist (`EmailMessage`,
  `EmailAttachment`, `ReplyTo`, `RenderTemplateAsync`, `ReadInboxAsync`,
  `ImapFilter`, `MarkAsReadAsync`, the `SendAsync(from:, to:, …)` /
  `SendTemplateAsync` overloads, and the `UseSsl` / `FromAddress` config keys).
- Documented the real surface: the fluent `MailBuilder` (`CreateMessage()`),
  `SmtpService.SendAsync(MailMessage)`, `MailLibrary.SendTemplatedAsync`, the
  `MailResult` / `MailError` result model, and the JSON-backed template system
  (`MailTemplate`, `IMailTemplateProvider`, `IMailTemplateEngine`).
- Documented previously undocumented features: template conditionals
  (`{{#if}}`/`{{#else}}`), loops (`{{#each}}`), sections and layouts; the
  `${var}` / `{var}` placeholder syntaxes; the full IMAP API (fetch/page, fetch
  by UID, search via `ImapSearchCriteria`, move/copy/delete, flag operations,
  folder management); and RFC 2177 IMAP IDLE push (`StartIdleAsync` /
  `NewMailReceived`) with its `EnableIdle` / `IdleRefreshMinutes` config keys.
- Corrected the documented config schema and `SecurityMode` values
  (`None` / `StartTls` / `SslTls`).

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

- Annotated mail configuration with `[ConfigField]` for the admin UI surface.
- Aligned with the v4 baseline across all libraries.

### Fixed

- Resolved null-reference warnings in `ImapService`.

## [4.0.0] — 2026-04-09

Major rewrite. Republished as v4.0.0 to reset the version line under the
unified v4 baseline. SMTP / IMAP services + a CodeLogic-native template
provider.

### Notes

- Earlier history is retained in the
  [git log](https://github.com/Media2A/CodeLogic.Libs/commits/main/CL.Mail).
