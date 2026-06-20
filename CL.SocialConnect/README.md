# CodeLogic.SocialConnect

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.SocialConnect)](https://www.nuget.org/packages/CodeLogic.SocialConnect)

Discord webhook and Steam Web API integration for [CodeLogic](https://github.com/Media2A/CodeLogic). Send Discord notifications and fetch Steam player profiles, bans, and game libraries.

## Install

```bash
dotnet add package CodeLogic.SocialConnect
```

## Quick Start

```csharp
await Libraries.LoadAsync<SocialConnectLibrary>();

var social = Libraries.Get<SocialConnectLibrary>();

// Discord — send a webhook text message
await social.Discord.SendMessageAsync("Server restarted");

// Steam — fetch player profile
var player = await social.Steam.GetPlayerAsync("76561198012345678");
if (player.IsSuccess)
    Console.WriteLine($"{player.Value.PersonaName} — {player.Value.ProfileUrl}");

// Steam — check bans
var bans = await social.Steam.GetPlayerBansAsync("76561198012345678");
```

Services throw `InvalidOperationException` if accessed while disabled. Guard with the
`HasDiscord` / `HasSteam` / `HasSteamAuth` properties when a service may be turned off.

## Features

### Discord
- **Webhook Messages** — `SendMessageAsync` (text), `SendEmbedAsync` (rich embeds), or `SendAsync` (full `DiscordWebhookMessage` payload)
- **Rich Embeds** — title, description, color, fields, author, footer, image, thumbnail, timestamp
- **Mention Control** — `DiscordAllowedMentions` (with `.None` / `.All` helpers) to whitelist which roles/users/`@everyone` may ping
- **TTS** — opt-in text-to-speech messages via `DiscordWebhookMessage.Tts`
- **Default Webhook URL** — set once in config, override per call; default username + avatar applied automatically
- **`DiscordUser` model** — username, avatar (with `GetAvatarUrl()` helper), bot/MFA/verified flags

### Steam
- **Player Profiles** — persona name, avatar (3 sizes), profile URL, real name, country, online status, plus `IsPublic` / `IsInGame` / `AccountCreated` helpers
- **Player Bans** — VAC bans, game bans, economy/trade bans, community bans, plus `HasAnyBan` helper
- **Game Library** — owned games with playtime (`TotalPlaytime` / `RecentPlaytime`), `LastPlayed`, and `GetIconUrl()`; `includeAppInfo` toggle on `GetOwnedGamesAsync`
- **Built-in Cache** — configurable TTL (default 5 min) to reduce API calls; clear on demand with `Steam.ClearCache()`
- **Steam Authentication** — ticket-based auth for game server integration (per-call `appId` override)

### Common
- **`Result` / `Result<T>` returns** — every call returns a typed result; check `IsSuccess` before reading `.Value`. Failure codes are categorized by the `SocialError` enum.
- **Event bus integration** — publishes `WebhookSentEvent`, `SteamProfileFetchedEvent`, and `SteamAuthenticatedEvent` for observability
- **Health check** — `HealthCheckAsync()` reports which services are active

## Configuration

Auto-generated at `data/codelogic/Libraries/CL.SocialConnect/config.socialconnect.json`:

```json
{
  "Enabled": true,
  "Discord": {
    "Enabled": true,
    "DefaultWebhookUrl": "https://discord.com/api/webhooks/xxx/yyy",
    "DefaultUsername": "MyApp",
    "DefaultAvatarUrl": "https://example.com/bot.png",
    "TimeoutSeconds": 10
  },
  "Steam": {
    "Enabled": true,
    "ApiKey": "your-steam-web-api-key",
    "AuthEnabled": false,
    "AppId": "480",
    "CacheTtlSeconds": 300,
    "TimeoutSeconds": 15,
    "ApiBaseUrl": "https://api.steampowered.com"
  }
}
```

> `Steam.Enabled` requires `ApiKey`. `Steam.AuthEnabled` additionally requires `AppId`
> (your game's Steam App ID). Steam is disabled by default.

Get your Steam Web API key at [steamcommunity.com/dev/apikey](https://steamcommunity.com/dev/apikey).

## Documentation

- [Social Integrations Guide](../docs/articles/social.md)

## Requirements

- [CodeLogic 3.x or 4.x](https://github.com/Media2A/CodeLogic) | .NET 10

## License

MIT — see [LICENSE](https://github.com/Media2A/CodeLogic.Libs/blob/main/LICENSE)
