# CodeLogic.Mail

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.Mail)](https://www.nuget.org/packages/CodeLogic.Mail)

SMTP/IMAP e-mail library for [CodeLogic](https://github.com/Media2A/CodeLogic) with a
built-in variable-substitution template engine (variables, conditionals, loops, and
layouts), attachment support, and full IMAP mailbox management including RFC 2177 IDLE
push notifications. Built on [MailKit](https://github.com/jstedfast/MailKit).

## Install

```bash
dotnet add package CodeLogic.Mail
```

## Quick Start

```csharp
using CL.Mail;

await Libraries.LoadAsync<MailLibrary>();
var mail = Libraries.Get<MailLibrary>()!;

// Compose with the fluent builder and send via SMTP.
var message = mail.CreateMessage()
    .From("noreply@example.com", "My App")
    .To("user@example.com")
    .Subject("Welcome!")
    .HtmlBody("<h1>Hello</h1><p>Welcome aboard.</p>")
    .TextBody("Hello — welcome aboard.")
    .Build();

MailResult result = await mail.Smtp.SendAsync(message);
if (!result.IsSuccess)
    Console.WriteLine($"Send failed ({result.Error}): {result.ErrorMessage}");
```

## Features

- **SMTP sending** — plain-text and/or HTML bodies, CC/BCC, attachments, custom headers,
  message priority. A fresh connection is opened per send (no pooling).
- **Fluent `MailBuilder`** — compose immutable `MailMessage` objects with validation.
- **Template engine** — JSON-backed templates with `{{var}}`, `${var}`, and `{var}`
  substitution, `{{#if}}…{{#else}}…{{/if}}` conditionals, `{{#each}}…{{/each}}` loops,
  and `{{#section}}` / layout composition.
- **Templated send** — `SendTemplatedAsync` renders a template by ID and sends in one call.
- **IMAP reading** — list folders, fetch/page messages, fetch by UID with attachments,
  search, move/copy/delete, and add/remove flags.
- **IMAP IDLE** — RFC 2177 push notifications via `StartIdleAsync` and the
  `NewMailReceived` event.
- **Result-based errors** — operations return `MailResult` / `MailResult<T>` with a
  categorised `MailError` instead of throwing.
- **Health checks** — reports SMTP/IMAP configuration status.

## Configuration

Registered under the `mail` section (serialized to
`config.mail.json` in the library's config directory):

```json
{
  "Enabled": true,
  "Smtp": {
    "Host": "smtp.example.com",
    "Port": 587,
    "Username": "noreply@example.com",
    "Password": "your-password-or-app-password",
    "SecurityMode": "StartTls",
    "TimeoutSeconds": 30
  },
  "Imap": {
    "Host": "imap.example.com",
    "Port": 993,
    "Username": "inbox@example.com",
    "Password": "your-password-or-app-password",
    "SecurityMode": "SslTls",
    "TimeoutSeconds": 30,
    "EnableIdle": false,
    "IdleRefreshMinutes": 25
  },
  "DefaultFromEmail": "noreply@example.com",
  "DefaultFromName": "My App",
  "TemplateDirectory": "templates"
}
```

- `SecurityMode` is one of `None`, `StartTls`, or `SslTls`.
- The whole `Imap` section is optional — omit it to disable IMAP entirely.
- `TemplateDirectory` is resolved relative to the library's data directory unless an
  absolute path is given; it defaults to a `templates` subfolder.

## Documentation

- [Mail & Templates Guide](https://github.com/Media2A/CodeLogic.Libs/blob/main/docs/articles/mail.md)

## Requirements

- [CodeLogic 3.x or 4.x](https://github.com/Media2A/CodeLogic) | .NET 10

## License

MIT — see [LICENSE](https://github.com/Media2A/CodeLogic.Libs/blob/main/LICENSE)
