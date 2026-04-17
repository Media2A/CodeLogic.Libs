# CodeLogic.Mail

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.Mail)](https://www.nuget.org/packages/CodeLogic.Mail)

SMTP/IMAP email library for [CodeLogic 3](https://github.com/Media2A/CodeLogic) with an HTML template engine, attachment support, and mailbox management. Built on [MailKit](https://github.com/jstedfast/MailKit).

## Install

```bash
dotnet add package CodeLogic.Mail
```

## Quick Start

```csharp
await Libraries.LoadAsync<MailLibrary>();

var mail = Libraries.Get<MailLibrary>();

// Send a simple email
await mail.Smtp.SendAsync(
    from: "noreply@example.com",
    to: "user@example.com",
    subject: "Welcome!",
    htmlBody: "<h1>Hello</h1><p>Welcome aboard.</p>"
);

// Send with the template engine
await mail.Smtp.SendTemplateAsync(
    from: "noreply@example.com",
    to: "user@example.com",
    subject: "Your order is ready",
    templatePath: "templates/order-confirmation.html",
    model: new { OrderId = "12345", CustomerName = "Alice" }
);
```

## Features

- **SMTP** — send plain-text and HTML emails, CC/BCC, reply-to, custom headers
- **Template Engine** — file-based HTML templates with model binding (`{{PropertyName}}`)
- **Attachments** — attach files from streams or paths
- **IMAP** — read mailboxes, fetch messages, move/delete, folder management
- **Security** — SSL/TLS and STARTTLS support
- **Health Checks** — verifies SMTP connectivity

## Configuration

Auto-generated at `data/codelogic/Libraries/CL.Mail/config.mail.json`:

```json
{
  "enabled": true,
  "smtp": {
    "host": "smtp.example.com",
    "port": 587,
    "username": "noreply@example.com",
    "password": "your-password",
    "securityMode": "StartTls",
    "timeoutSeconds": 30
  },
  "defaultFromEmail": "noreply@example.com",
  "defaultFromName": "My App",
  "templateDirectory": "templates"
}
```

## Documentation

- [Mail & Templates Guide](../docs/articles/mail.md)

## Requirements

- [CodeLogic 3.x](https://github.com/Media2A/CodeLogic) | .NET 10

## License

MIT — see [LICENSE](../LICENSE)
