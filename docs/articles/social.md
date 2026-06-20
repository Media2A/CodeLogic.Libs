# Social & Webhooks - CL.SocialConnect

CL.SocialConnect provides Discord webhook delivery plus Steam Web API profile lookups and ticket-based authentication.

---

## Registration

```csharp
await Libraries.LoadAsync<CL.SocialConnect.SocialConnectLibrary>();
```

---

## Configuration (`config.socialconnect.json`)

```json
{
  "Enabled": true,
  "Discord": {
    "Enabled": true,
    "DefaultWebhookUrl": "https://discord.com/api/webhooks/123456789/XXXXX",
    "TimeoutSeconds": 10,
    "DefaultUsername": "MyApp Bot",
    "DefaultAvatarUrl": "https://example.com/bot.png"
  },
  "Steam": {
    "Enabled": true,
    "ApiKey": "your-steam-web-api-key",
    "AuthEnabled": true,
    "AppId": "480",
    "CacheTtlSeconds": 300,
    "TimeoutSeconds": 15,
    "ApiBaseUrl": "https://api.steampowered.com"
  }
}
```

Steam is **disabled by default**. When `Steam.Enabled` is `true`, `ApiKey` is required; when `Steam.AuthEnabled` is `true`, `AppId` is also required.

---

## Discord Webhooks

### Simple Text Message

```csharp
var social = context.GetLibrary<CL.SocialConnect.SocialConnectLibrary>();

await social.Discord.SendMessageAsync("Server started successfully!");
```

### Rich Embed

```csharp
using CL.SocialConnect.Models.Discord;

await social.Discord.SendEmbedAsync(
[
    new DiscordEmbed
    {
        Title = "Deployment Complete",
        Description = "Version **1.4.2** deployed to production",
        Color = 0x2ECC71,
        Fields =
        [
            new DiscordEmbedField { Name = "Environment", Value = "Production", Inline = true },
            new DiscordEmbedField { Name = "Version", Value = "1.4.2", Inline = true },
            new DiscordEmbedField { Name = "Duration", Value = "2m 34s", Inline = true }
        ],
        Footer = new DiscordEmbedFooter { Text = "MyApp CI/CD" },
        Timestamp = DateTime.UtcNow
    }
]);
```

### Override the Webhook Per Message

```csharp
await social.Discord.SendMessageAsync(
    "Database connection pool exhausted!",
    "https://discord.com/api/webhooks/123456789/override-token");
```

### Full Payload, Mention Control & TTS

For full control over the payload, build a `DiscordWebhookMessage` and call `SendAsync`. Use `DiscordAllowedMentions` to whitelist which mentions are allowed to ping — `DiscordAllowedMentions.None` suppresses every ping (a safe default for automated notifications), and `DiscordAllowedMentions.All` permits roles, users, and `@everyone`.

```csharp
using CL.SocialConnect.Models.Discord;

await social.Discord.SendAsync(new DiscordWebhookMessage
{
    Content = "<@&123456789012345678> deployment finished",
    Tts = false,
    // Only allow the named role to ping; @everyone is ignored.
    AllowedMentions = new DiscordAllowedMentions { Roles = ["123456789012345678"] }
});
```

Every send returns a `Result`; check `IsSuccess` before assuming delivery:

```csharp
var result = await social.Discord.SendMessageAsync("Heartbeat");
if (!result.IsSuccess)
    Console.WriteLine($"Discord send failed: {result.Error.Message}");
```

---

## Steam Ticket Authentication

CL.SocialConnect does not use OpenID redirects. Authentication is done by validating a session ticket from the Steam client against `ISteamUserAuth/AuthenticateUserTicket`.

```csharp
var social = context.GetLibrary<CL.SocialConnect.SocialConnectLibrary>();

var authResult = await social.Auth.AuthenticateAsync(ticketFromClient);
if (!authResult.IsSuccess)
{
    return Results.BadRequest("Steam authentication failed");
}

var steamId = authResult.Value.SteamId;
```

`AuthenticateAsync` falls back to `Steam.AppId` from configuration, but you can validate against a different App ID per call:

```csharp
var authResult = await social.Auth.AuthenticateAsync(ticketFromClient, appId: "440");
```

---

## Steam Profiles

```csharp
var player = await social.Steam.GetPlayerAsync("76561198012345678");
if (player.IsSuccess)
{
    var p = player.Value;
    Console.WriteLine(p.PersonaName);
    Console.WriteLine(p.ProfileUrl);
    Console.WriteLine(p.AvatarFull);

    // Computed helpers
    Console.WriteLine($"Public profile: {p.IsPublic}");
    Console.WriteLine($"In game: {p.IsInGame} ({p.GameExtraInfo})");
    Console.WriteLine($"Account created: {p.AccountCreated:d}");
}
```

### Bans

```csharp
var bans = await social.Steam.GetPlayerBansAsync("76561198012345678");
if (bans.IsSuccess && bans.Value.HasAnyBan)
{
    var b = bans.Value;
    Console.WriteLine($"VAC: {b.VacBanned} ({b.NumberOfVacBans}), " +
                      $"Game bans: {b.NumberOfGameBans}, Economy: {b.EconomyBan}");
}
```

### Owned Games

`GetOwnedGamesAsync` accepts `includeAppInfo` (default `true`) to control whether game names and icon data are returned. Each `SteamGame` exposes playtime as `TimeSpan` and an icon URL helper.

```csharp
var games = await social.Steam.GetOwnedGamesAsync("76561198012345678", includeAppInfo: true);
if (games.IsSuccess)
{
    foreach (var g in games.Value)
    {
        Console.WriteLine($"{g.Name}: {g.TotalPlaytime.TotalHours:F1}h total");
        if (g.RecentPlaytime is { } recent)
            Console.WriteLine($"  last 2 weeks: {recent.TotalHours:F1}h");
        Console.WriteLine($"  last played: {g.LastPlayed:d}");
        Console.WriteLine($"  icon: {g.GetIconUrl()}");
    }
}
```

### Cache

Profile, ban, and game results are cached for `Steam.CacheTtlSeconds`. Flush all caches manually when you need fresh data:

```csharp
social.Steam.ClearCache();
```

---

## Events

The library publishes events on the CodeLogic event bus so you can observe webhook and Steam activity:

| Event | Published when |
| --- | --- |
| `WebhookSentEvent` | After every Discord webhook send attempt (success or failure). |
| `SteamProfileFetchedEvent` | After a player profile is fetched (`FromCache` indicates a cache hit). |
| `SteamAuthenticatedEvent` | After a Steam ticket validation attempt completes. |

```csharp
using CL.SocialConnect.Events;

context.Events.Subscribe<WebhookSentEvent>(e =>
    context.Logger.Info($"Webhook → {e.StatusCode} success={e.Success}"));
```

---

## Service Availability

`Discord`, `Steam`, and `Auth` throw `InvalidOperationException` when their service is disabled in configuration. Probe availability first with the boolean helpers:

```csharp
if (social.HasDiscord) await social.Discord.SendMessageAsync("Up");
if (social.HasSteam)   { /* … */ }
if (social.HasSteamAuth) { /* … */ }
```

---

## Health Check

```csharp
var status = await social.HealthCheckAsync();
```
