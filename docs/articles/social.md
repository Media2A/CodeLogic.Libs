# Social & Webhooks — CL.SocialConnect

CL.SocialConnect provides Discord webhook integration with rich embeds and Steam OpenID 2.0 authentication.

---

## Registration

```csharp
await Libraries.LoadAsync<CL.SocialConnect.SocialConnectLibrary>();
```

---

## Configuration (`config.socialconnect.json`)

```json
{
  "Discord": {
    "DefaultWebhookUrl": "https://discord.com/api/webhooks/123456789/XXXXX",
    "Webhooks": {
      "alerts":  "https://discord.com/api/webhooks/111111111/AAAAA",
      "deploys": "https://discord.com/api/webhooks/222222222/BBBBB"
    }
  },
  "Steam": {
    "ReturnUrl":  "https://myapp.com/auth/steam/callback",
    "Realm":      "https://myapp.com/"
  }
}
```

---

## Discord Webhooks

### Simple Text Message

```csharp
var social = context.GetLibrary<CL.SocialConnect.SocialConnectLibrary>();

await social.Discord.SendAsync("Server started successfully!");
```

### Rich Embed

```csharp
await social.Discord.SendEmbedAsync(new DiscordEmbed
{
    Title       = "Deployment Complete",
    Description = "Version **1.4.2** deployed to production",
    Color       = DiscordColor.Green,
    Fields      =
    [
        new DiscordField("Environment", "Production", inline: true),
        new DiscordField("Version",     "1.4.2",      inline: true),
        new DiscordField("Duration",    "2m 34s",     inline: true),
        new DiscordField("Deployed by", "CI/CD Pipeline")
    ],
    Footer    = new DiscordFooter("MyApp CI/CD"),
    Timestamp = DateTime.UtcNow
});
```

### Sending to a Named Webhook

```csharp
// Sends to the "alerts" webhook configured above
await social.Discord.SendAsync("Database connection pool exhausted!", webhookName: "alerts");

await social.Discord.SendEmbedAsync(embed, webhookName: "deploys");
```

### Alert Embed Helper

```csharp
// Pre-built alert embed with severity colors:
await social.Discord.SendAlertAsync(
    title:    "High CPU Usage",
    message:  "CPU has been above 90% for 5 minutes",
    severity: AlertSeverity.Warning,
    webhook:  "alerts"
);
```

| Severity | Color |
|----------|-------|
| `Info` | Blue |
| `Warning` | Yellow |
| `Error` | Orange |
| `Critical` | Red |
| `Success` | Green |

---

## Discord Embed Reference

```csharp
public sealed class DiscordEmbed
{
    public string? Title       { get; set; }
    public string? Description { get; set; }
    public DiscordColor Color  { get; set; }   // hex color
    public string? Url         { get; set; }   // title link
    public DiscordAuthor? Author { get; set; }
    public DiscordThumbnail? Thumbnail { get; set; }
    public DiscordImage? Image { get; set; }
    public List<DiscordField> Fields { get; set; } = [];
    public DiscordFooter? Footer { get; set; }
    public DateTime? Timestamp { get; set; }
}

public sealed class DiscordField
{
    public string Name   { get; set; }
    public string Value  { get; set; }
    public bool Inline   { get; set; }
}
```

---

## Steam OpenID Authentication

Steam uses OpenID 2.0 for authentication. CL.SocialConnect handles the redirect and verification.

### Step 1: Generate the Steam Login URL

```csharp
var social = context.GetLibrary<CL.SocialConnect.SocialConnectLibrary>();

// Generate the URL to redirect the user to Steam for login
string loginUrl = social.Steam.GetLoginUrl();

// In an ASP.NET Core endpoint:
app.MapGet("/auth/steam", () => Results.Redirect(loginUrl));
```

### Step 2: Handle the Callback

After the user logs in on Steam, they are redirected back to your `ReturnUrl`:

```csharp
app.MapGet("/auth/steam/callback", async (HttpRequest request, IServiceProvider sp) =>
{
    var social = sp.GetRequiredService<CL.SocialConnect.SocialConnectLibrary>();

    // Verify the OpenID response and extract the Steam ID
    var result = await social.Steam.ValidateCallbackAsync(request.Query);

    if (!result.IsSuccess)
        return Results.BadRequest("Steam authentication failed");

    var steamId = result.SteamId;
    // e.g. "76561198012345678"

    // Look up or create the user
    var user = await userRepo.FindAsync(u => u.SteamId == steamId)
               ?? await CreateSteamUserAsync(steamId);

    // Issue your own session/JWT
    return Results.Ok(new { token = GenerateJwt(user) });
});
```

### Step 3: Fetch Steam Profile (Optional)

```csharp
var profile = await social.Steam.GetProfileAsync(steamId);

Console.WriteLine($"Username: {profile.PersonaName}");
Console.WriteLine($"Avatar:   {profile.AvatarUrl}");
Console.WriteLine($"Country:  {profile.CountryCode}");
Console.WriteLine($"State:    {profile.ProfileState}");   // Online, Away, Offline
```

### SteamProfile

```csharp
public sealed class SteamProfile
{
    public string SteamId      { get; init; }
    public string PersonaName  { get; init; }   // display name
    public string AvatarUrl    { get; init; }
    public string ProfileUrl   { get; init; }
    public string? CountryCode { get; init; }
    public string? StateCode   { get; init; }
    public SteamProfileState ProfileState { get; init; }
    public DateTime LastLogOff { get; init; }
}
```

---

## Health Check

```csharp
// Checks Discord webhook reachability and Steam API availability
var status = await social.HealthCheckAsync();
```
