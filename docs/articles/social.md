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
    "TimeoutSeconds": 15
  }
}
```

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

---

## Steam Profiles

```csharp
var player = await social.Steam.GetPlayerAsync("76561198012345678");
if (player.IsSuccess)
{
    Console.WriteLine(player.Value.PersonaName);
    Console.WriteLine(player.Value.ProfileUrl);
    Console.WriteLine(player.Value.AvatarFull);
}

var bans = await social.Steam.GetPlayerBansAsync("76561198012345678");
var games = await social.Steam.GetOwnedGamesAsync("76561198012345678");
```

---

## Health Check

```csharp
var status = await social.HealthCheckAsync();
```
