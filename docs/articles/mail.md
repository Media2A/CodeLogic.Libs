# Mail — CL.Mail

CL.Mail provides SMTP email sending and IMAP reading with a Handlebars-style template engine, HTML and plain-text support, and file attachments.

---

## Registration

```csharp
await Libraries.LoadAsync<CL.Mail.MailLibrary>();
```

---

## Configuration (`config.mail.json`)

```json
{
  "Smtp": {
    "Host": "smtp.example.com",
    "Port": 587,
    "UseSsl": true,
    "Username": "noreply@example.com",
    "Password": "secret",
    "FromAddress": "noreply@example.com",
    "FromName": "My Application"
  },
  "Imap": {
    "Host": "imap.example.com",
    "Port": 993,
    "UseSsl": true,
    "Username": "inbox@example.com",
    "Password": "secret"
  },
  "TemplatesPath": "templates/"
}
```

---

## Sending Email

### Simple HTML Email

```csharp
var mail = context.GetLibrary<CL.Mail.MailLibrary>();

await mail.SendAsync(new EmailMessage
{
    To      = ["alice@example.com"],
    Subject = "Welcome to MyApp",
    HtmlBody = "<h1>Welcome!</h1><p>Your account is ready.</p>",
    TextBody = "Welcome! Your account is ready."
});
```

### With CC, BCC, and Reply-To

```csharp
await mail.SendAsync(new EmailMessage
{
    To       = ["alice@example.com"],
    Cc       = ["manager@example.com"],
    Bcc      = ["audit@example.com"],
    ReplyTo  = "support@example.com",
    Subject  = "Order Confirmation #12345",
    HtmlBody = "<p>Your order has been confirmed.</p>"
});
```

### With Attachments

```csharp
await mail.SendAsync(new EmailMessage
{
    To      = ["alice@example.com"],
    Subject = "Your Invoice",
    HtmlBody = "<p>Please find your invoice attached.</p>",
    Attachments =
    [
        new EmailAttachment
        {
            FileName    = "invoice-2026-04.pdf",
            ContentType = "application/pdf",
            Data        = await File.ReadAllBytesAsync("invoices/inv-2026-04.pdf")
        }
    ]
});
```

---

## Template Engine

CL.Mail includes a Handlebars-style template engine. Templates are HTML files with `{{variable}}` placeholders.

### Template File

Place templates in the library's `templates/` directory:

```
CodeLogic/Libraries/CL.Mail/templates/
  welcome.html
  order-confirmation.html
  password-reset.html
```

```html
<!-- templates/welcome.html -->
<!DOCTYPE html>
<html>
<body>
  <h1>Welcome, {{name}}!</h1>
  <p>Your account was created on {{date}}.</p>
  <p>Click <a href="{{activationUrl}}">here</a> to activate your account.</p>
  {{#if isPremium}}
  <p>You have <strong>premium</strong> access.</p>
  {{/if}}
</body>
</html>
```

### Using Templates

```csharp
var body = await mail.RenderTemplateAsync("welcome", new
{
    name          = "Alice",
    date          = DateTime.UtcNow.ToString("MMMM d, yyyy"),
    activationUrl = "https://myapp.com/activate?token=abc123",
    isPremium     = true
});

await mail.SendAsync(new EmailMessage
{
    To      = ["alice@example.com"],
    Subject = "Welcome to MyApp",
    HtmlBody = body
});
```

### Convenience Method

```csharp
await mail.SendTemplateAsync(
    to:       ["alice@example.com"],
    subject:  "Welcome to MyApp",
    template: "welcome",
    model:    new { name = "Alice", activationUrl = "https://..." }
);
```

---

## Reading Email (IMAP)

```csharp
var messages = await mail.ReadInboxAsync(maxMessages: 20);

foreach (var msg in messages)
{
    Console.WriteLine($"From: {msg.From}");
    Console.WriteLine($"Subject: {msg.Subject}");
    Console.WriteLine($"Body: {msg.TextBody}");

    if (msg.Subject.StartsWith("ALERT:"))
    {
        await ProcessAlertAsync(msg);
        await mail.MarkAsReadAsync(msg.Id);
    }
}
```

### Filtering

```csharp
// Unread messages from a specific sender
var support = await mail.ReadInboxAsync(new ImapFilter
{
    Unread     = true,
    From       = "client@bigcorp.com",
    MaxResults = 50
});
```

---

## Health Check

```csharp
// Returns Healthy if SMTP connection can be established
// Returns Degraded if SMTP is reachable but latency is high
// Returns Unhealthy if SMTP connection fails
var status = await mail.HealthCheckAsync();
```
