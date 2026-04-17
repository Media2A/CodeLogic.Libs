# CodeLogic.SocialConnect

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.SocialConnect)](https://www.nuget.org/packages/CodeLogic.SocialConnect)

Discord webhook and Steam Web API integration for [CodeLogic 3](https://github.com/Media2A/CodeLogic). Send Discord notifications and fetch Steam player profiles, bans, and game libraries.

## Install

```bash
dotnet add package CodeLogic.SocialConnect
```

## Quick Start

```csharp
await Libraries.LoadAsync<SocialConnectLibrary>();

var social = Libraries.Get<SocialConnectLibrary>();

// Discord — send a webhook message
await social.Discord.SendAsync("Server restarted", username: "Bot");

// Steam — fetch player profile
var player = await social.Steam.GetPlayerAsync("76561198012345678");
if (player.IsSuccess)
    Console.WriteLine($"{player.Value.PersonaName} — {player.Value.ProfileUrl}");

// Steam — check bans
var bans = await social.Steam.GetPlayerBansAsync("76561198012345678");
```

## Features

### Discord
- **Webhook Messages** — send text, embeds, mentions, with configurable username and avatar
- **Default Webhook URL** — set once in config, override per call
- **Rate Limit Aware** — respects Discord rate limits

### Steam
- **Player Profiles** — persona name, avatar (3 sizes), profile URL, real name, country, online status
- **Player Bans** — VAC bans, game bans, trade bans, community bans
- **Game Library** — owned games with playtime and app info
- **Built-in Cache** — configurable TTL (default 5 min) to reduce API calls
- **Steam Authentication** — ticket-based auth for game server integration

## Configuration

Auto-generated at `data/codelogic/Libraries/CL.SocialConnect/config.socialconnect.json`:

```json
{
  "enabled": true,
  "discord": {
    "enabled": true,
    "defaultWebhookUrl": "https://discord.com/api/webhooks/xxx/yyy",
    "defaultUsername": "MyApp",
    "timeoutSeconds": 10
  },
  "steam": {
    "enabled": true,
    "apiKey": "your-steam-web-api-key",
    "cacheTtlSeconds": 300,
    "timeoutSeconds": 15
  }
}
```

Get your Steam Web API key at [steamcommunity.com/dev/apikey](https://steamcommunity.com/dev/apikey).

## Documentation

- [Social Integrations Guide](../docs/articles/social.md)

## Requirements

- [CodeLogic 3.x](https://github.com/Media2A/CodeLogic) | .NET 10

## License

MIT — see [LICENSE](../LICENSE)
