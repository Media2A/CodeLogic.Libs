# Mail — CL.Mail

CL.Mail provides SMTP e-mail sending and IMAP reading with a lightweight
variable-substitution template engine (variables, conditionals, loops, and
layouts), HTML and plain-text bodies, and file attachments. It is built on
[MailKit](https://github.com/jstedfast/MailKit).

All operations return a `MailResult` (or `MailResult<T>`) carrying an
`IsSuccess` flag, a categorised `MailError`, and an `ErrorMessage` — the
services do not throw on protocol/auth/timeout errors.

---

## Registration

```csharp
using CL.Mail;

await Libraries.LoadAsync<MailLibrary>();
var mail = Libraries.Get<MailLibrary>()!;
```

The library exposes:

| Member | Purpose |
| --- | --- |
| `mail.Smtp` | `SmtpService` for sending. |
| `mail.Imap` | `ImapService` for reading (throws if IMAP is not configured). |
| `mail.HasImap` | `true` when an `Imap` config section is present. |
| `mail.CreateMessage()` | New fluent `MailBuilder`. |
| `mail.SendTemplatedAsync(...)` | Render a template by ID and send it. |
| `mail.TemplateProvider` | Load / list / save templates. |
| `mail.TemplateEngine` | Render a `MailTemplate` to text. |

---

## Configuration (`config.mail.json`)

The library registers a `mail` config section. The `Imap` section is optional —
omit it to disable IMAP support entirely.

```json
{
  "Enabled": true,
  "Smtp": {
    "Host": "smtp.example.com",
    "Port": 587,
    "Username": "noreply@example.com",
    "Password": "secret",
    "SecurityMode": "StartTls",
    "TimeoutSeconds": 30
  },
  "Imap": {
    "Host": "imap.example.com",
    "Port": 993,
    "Username": "inbox@example.com",
    "Password": "secret",
    "SecurityMode": "SslTls",
    "TimeoutSeconds": 30,
    "EnableIdle": false,
    "IdleRefreshMinutes": 25
  },
  "DefaultFromEmail": "noreply@example.com",
  "DefaultFromName": "My Application",
  "TemplateDirectory": "templates"
}
```

`SecurityMode` is one of:

| Value | Meaning |
| --- | --- |
| `None` | Unencrypted connection (not recommended). |
| `StartTls` | Upgrade to TLS in-band via STARTTLS (typical SMTP port 587). |
| `SslTls` | Implicit TLS from connect (typical SMTP port 465, IMAP port 993). |

`TemplateDirectory` is resolved relative to the library's data directory unless
an absolute path is supplied; it defaults to a `templates` subfolder.

---

## Sending Email

Messages are built with the fluent `MailBuilder` (via `mail.CreateMessage()`)
and sent with `mail.Smtp.SendAsync`.

### Simple HTML + text email

```csharp
var message = mail.CreateMessage()
    .From("noreply@example.com", "My App")
    .To("alice@example.com")
    .Subject("Welcome to MyApp")
    .HtmlBody("<h1>Welcome!</h1><p>Your account is ready.</p>")
    .TextBody("Welcome! Your account is ready.")
    .Build();

var result = await mail.Smtp.SendAsync(message);
if (result.IsSuccess)
    Console.WriteLine($"Sent, message id: {result.MessageId}");
else
    Console.WriteLine($"Failed ({result.Error}): {result.ErrorMessage}");
```

At least one of `HtmlBody` / `TextBody` is required. `Build()` throws
`InvalidOperationException` if the sender, recipients, subject, or body are
missing; `SendAsync` additionally returns a failure `MailResult` for an empty
or invalid message.

### CC, BCC, headers, and priority

```csharp
var message = mail.CreateMessage()
    .From("noreply@example.com")
    .To("alice@example.com", "bob@example.com")   // multiple recipients
    .Cc("manager@example.com")
    .Bcc("audit@example.com")
    .Subject("Order Confirmation #12345")
    .HtmlBody("<p>Your order has been confirmed.</p>")
    .Header("X-Mailer", "MyApp")
    .Priority(MailPriority.High)
    .Build();

await mail.Smtp.SendAsync(message);
```

### Attachments

Attachments are added by absolute file path. Paths that do not exist on disk are
silently skipped when the MIME message is built.

```csharp
var message = mail.CreateMessage()
    .From("noreply@example.com")
    .To("alice@example.com")
    .Subject("Your Invoice")
    .HtmlBody("<p>Please find your invoice attached.</p>")
    .Attach(@"C:\invoices\inv-2026-04.pdf")
    .Build();

await mail.Smtp.SendAsync(message);
```

---

## Template Engine

Templates are stored as JSON files (`{id}.json`) in the configured template
directory by the built-in `FileMailTemplateProvider`. Each template has a
subject and an optional text and/or HTML body. The `SimpleTemplateEngine`
substitutes variables and processes conditionals, loops, and layouts.

### Supported syntax

| Syntax | Meaning |
| --- | --- |
| `{{var}}`, `${var}`, `{var}` | Variable substitution (lookup is case-insensitive). |
| `{{#if var}}…{{/if}}` | Conditional block (rendered when `var` is truthy). |
| `{{#if var}}…{{#else}}…{{/if}}` | Conditional with an else branch. |
| `{{#each items}}…{{/each}}` | Loop over a collection; item properties become variables. |
| `{{#section name}}…{{/section}}` | Named section injected into a layout. |

Unknown placeholders are left untouched. A value is "truthy" when it is a
non-zero number, `true`, or a non-empty string other than `"false"` / `"0"`.

### A template file

`templates/welcome.json`:

```json
{
  "Id": "welcome",
  "Name": "Welcome Email",
  "Subject": "Welcome, {{name}}!",
  "HtmlBody": "<h1>Welcome, {{name}}!</h1>\n<p>Your account was created on {{date}}.</p>\n<p>Click <a href=\"{{activationUrl}}\">here</a> to activate.</p>\n{{#if isPremium}}<p>You have <strong>premium</strong> access.</p>{{/if}}",
  "TextBody": "Welcome, {{name}}! Activate: {{activationUrl}}"
}
```

You can also create and save templates from code:

```csharp
var template = MailTemplate.Create(
    id: "welcome",
    subject: "Welcome, {{name}}!",
    htmlBody: "<h1>Welcome, {{name}}!</h1>",
    textBody: "Welcome, {{name}}!");

await mail.TemplateProvider.SaveTemplateAsync(template);

// Discover available templates:
var idsResult = await mail.TemplateProvider.ListTemplatesAsync();
```

### Render and send in one call

`SendTemplatedAsync` loads the template by ID, renders it with the supplied
variables, and sends the result. The `configure` callback receives the
`MailBuilder` (with subject and bodies pre-filled) so you can set sender and
recipients:

```csharp
var result = await mail.SendTemplatedAsync(
    templateId: "welcome",
    variables: new Dictionary<string, object?>
    {
        ["name"]          = "Alice",
        ["date"]          = DateTime.UtcNow.ToString("MMMM d, yyyy"),
        ["activationUrl"] = "https://myapp.com/activate?token=abc123",
        ["isPremium"]     = true
    },
    configure: builder => builder
        .From("noreply@example.com", "My App")
        .To("alice@example.com"));
```

### Render manually

To render without sending, load the template and call the engine directly:

```csharp
var load = await mail.TemplateProvider.LoadTemplateAsync("welcome");
if (load.IsSuccess)
{
    var rendered = await mail.TemplateEngine.RenderAsync(load.Value!,
        new Dictionary<string, object?> { ["name"] = "Alice" });

    // rendered.Value!.Subject / .HtmlBody / .TextBody
}
```

### Loops and layouts

A loop iterates a collection; each item's properties (or dictionary keys) become
variables inside the block:

```text
{{#each items}}<li>{{name}} — {{price}}</li>{{/each}}
```

```csharp
var vars = new Dictionary<string, object?>
{
    ["items"] = new[]
    {
        new { name = "Widget", price = "$9.99" },
        new { name = "Gadget", price = "$14.50" }
    }
};
```

A template may reference a `Layout` (another template's ID). The child body is
passed to the layout as the `{{body}}` variable, and any `{{#section name}}…
{{/section}}` blocks in the child are exposed to the layout as `{{name}}`.

---

## Reading Email (IMAP)

IMAP access is available through `mail.Imap` when the `Imap` section is
configured (`mail.HasImap` reports availability). Connections open on demand.

### Fetch a page of messages

```csharp
var result = await mail.Imap.FetchMessagesAsync(
    folder: "INBOX", offset: 0, count: 20, includeBody: true);

if (result.IsSuccess)
{
    foreach (var msg in result.Value!)
    {
        Console.WriteLine($"From:    {msg.FromName} <{msg.From}>");
        Console.WriteLine($"Subject: {msg.Subject}");
        Console.WriteLine($"Body:    {msg.TextBody}");

        if (msg.Subject?.StartsWith("ALERT:") == true)
            await mail.Imap.SetMessageFlagsAsync("INBOX", msg.Uid, MessageFlags.Seen, add: true);
    }
}
```

Messages are returned newest-first. Set `includeBody: false` (the default) to
fetch only envelope + flags for faster listing. Fetch a single message with full
body and attachment content by UID with `GetMessageAsync`:

```csharp
var one = await mail.Imap.GetMessageAsync("INBOX", uid);
```

### Searching

`ImapSearchCriteria` fields are ANDed together. Convenience factories
`ImapSearchCriteria.All()` and `ImapSearchCriteria.Unread()` cover the common
cases.

```csharp
var support = await mail.Imap.SearchAsync(
    folder: "INBOX",
    criteria: new ImapSearchCriteria
    {
        From    = "client@bigcorp.com",
        NotFlags = MessageFlags.Seen,        // unread only
        Since   = DateTime.UtcNow.AddDays(-7)
    },
    includeBody: false,
    maxResults: 50);
```

### Flags, move, copy, delete

```csharp
// Mark read / unread
await mail.Imap.SetMessageFlagsAsync("INBOX", uid, MessageFlags.Seen, add: true);

// Move / copy between folders
await mail.Imap.MoveMessageAsync("INBOX", uid, "INBOX/Archive");
await mail.Imap.CopyMessageAsync("INBOX", uid, "INBOX/Backup");

// Permanently delete (marks \Deleted and expunges)
await mail.Imap.DeleteMessageAsync("INBOX", uid);
```

### Folder management

```csharp
var folders = await mail.Imap.ListFoldersAsync();   // includes INBOX + counts
await mail.Imap.CreateFolderAsync("Projects");
await mail.Imap.RenameFolderAsync("Projects", "Active Projects");
await mail.Imap.DeleteFolderAsync("Active Projects");
```

### Push notifications (IDLE)

When the server advertises the IDLE capability, `StartIdleAsync` monitors a
folder on a background loop and raises `NewMailReceived` when new messages
arrive. The connection is refreshed every `IdleRefreshMinutes` (RFC 2177
recommends under 29 minutes).

```csharp
mail.Imap.NewMailReceived += (_, e) =>
    Console.WriteLine($"New mail in {e.FolderName} (now {e.MessageCount} messages)");

await mail.Imap.StartIdleAsync("INBOX");
// ... later ...
mail.Imap.StopIdle();
```

IDLE is also stopped automatically when the library stops or the service is
disposed.

---

## Error handling

Operations report failures through the result type rather than throwing:

```csharp
var result = await mail.Smtp.SendAsync(message);
switch (result.Error)
{
    case MailError.None:                       /* success */ break;
    case MailError.SmtpAuthenticationFailed:   /* bad credentials */ break;
    case MailError.SmtpTimeout:                /* connection timed out */ break;
    case MailError.InvalidRecipient:           /* missing/empty recipients */ break;
    // ... see the MailError enum for the full list
}
```

---

## Health Check

`HealthCheckAsync` reports the configured endpoints:

- **Healthy** — SMTP host is configured (and IMAP, when present).
- **Degraded** — SMTP host is missing.
- **Unhealthy** — the library failed to initialize.
- **Healthy ("disabled")** — `Enabled` is `false`.

```csharp
var status = await mail.HealthCheckAsync();
```
