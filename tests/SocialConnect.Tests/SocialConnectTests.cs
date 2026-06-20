using CL.SocialConnect;
using CL.SocialConnect.Models;
using CL.SocialConnect.Models.Discord;
using CL.SocialConnect.Models.Steam;
using CL.SocialConnect.Services.Discord;
using CodeLogic;                     // Libraries
using Xunit;

namespace SocialConnect.Tests;

// ── CL.SocialConnect tests ───────────────────────────────────────────────────────
// HYBRID strategy:
//   • Steam/Discord *model* computed properties and the Discord limit-validation
//     regression are pure, offline, in-memory checks — exercised directly with no boot
//     and no network (Discord validation short-circuits before the HTTP call).
//   • Actually calling the Steam Web API requires network + a real API key, so that
//     single test is env-gated and SKIPS unless CL_STEAM_TEST_APIKEY is provided.

// ── Steam model computed properties (offline) ─────────────────────────────────────

public sealed class SteamPlayerTests
{
    [Theory]
    [InlineData(3, true)]
    [InlineData(1, false)]
    public void IsPublic_reflects_community_visibility_state(int state, bool expected)
    {
        var player = new SteamPlayer { CommunityVisibilityState = state };
        Assert.Equal(expected, player.IsPublic);
    }

    [Fact]
    public void IsInGame_is_true_when_game_id_set()
    {
        Assert.True(new SteamPlayer { GameId = "730" }.IsInGame);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsInGame_is_false_when_game_id_missing(string? gameId)
    {
        Assert.False(new SteamPlayer { GameId = gameId }.IsInGame);
    }

    [Fact]
    public void AccountCreated_matches_unix_timestamp_in_utc()
    {
        // 2015-06-15T12:34:56Z
        long unix = 1434371696;
        var player = new SteamPlayer { TimeCreated = unix };

        var expected = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
        Assert.Equal(expected, player.AccountCreated);
        Assert.Equal(DateTimeKind.Utc, player.AccountCreated!.Value.Kind);
    }

    [Fact]
    public void AccountCreated_is_null_when_timestamp_missing()
    {
        Assert.Null(new SteamPlayer { TimeCreated = null }.AccountCreated);
    }
}

public sealed class SteamGameTests
{
    [Fact]
    public void TotalPlaytime_converts_minutes_to_timespan()
    {
        var game = new SteamGame { PlaytimeForever = 120 };
        Assert.Equal(TimeSpan.FromMinutes(120), game.TotalPlaytime);
    }

    [Fact]
    public void RecentPlaytime_has_value_when_two_week_playtime_set()
    {
        var game = new SteamGame { Playtime2Weeks = 45 };
        Assert.Equal(TimeSpan.FromMinutes(45), game.RecentPlaytime);
    }

    [Fact]
    public void RecentPlaytime_is_null_when_two_week_playtime_missing()
    {
        Assert.Null(new SteamGame { Playtime2Weeks = null }.RecentPlaytime);
    }

    [Fact]
    public void LastPlayed_matches_unix_timestamp_in_utc()
    {
        long unix = 1434371696;
        var game = new SteamGame { RtimeLastPlayed = unix };

        var expected = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
        Assert.Equal(expected, game.LastPlayed);
        Assert.Equal(DateTimeKind.Utc, game.LastPlayed!.Value.Kind);
    }

    [Fact]
    public void LastPlayed_is_null_when_timestamp_missing()
    {
        Assert.Null(new SteamGame { RtimeLastPlayed = null }.LastPlayed);
    }

    [Fact]
    public void GetIconUrl_contains_appid_and_hash_when_icon_set()
    {
        var game = new SteamGame { AppId = 730, ImgIconUrl = "deadbeefhash" };

        var url = game.GetIconUrl();

        Assert.NotNull(url);
        Assert.Contains("730", url);
        Assert.Contains("deadbeefhash", url);
    }

    [Fact]
    public void GetIconUrl_is_null_when_icon_missing()
    {
        Assert.Null(new SteamGame { AppId = 730, ImgIconUrl = null }.GetIconUrl());
    }
}

public sealed class SteamPlayerBansTests
{
    [Fact]
    public void HasAnyBan_is_true_when_vac_banned()
    {
        Assert.True(new SteamPlayerBans { VacBanned = true }.HasAnyBan);
    }

    [Fact]
    public void HasAnyBan_is_false_when_all_clean()
    {
        var bans = new SteamPlayerBans
        {
            CommunityBanned = false,
            VacBanned = false,
            NumberOfGameBans = 0,
            EconomyBan = "none"
        };
        Assert.False(bans.HasAnyBan);
    }
}

// ── Discord POCO round-trip (offline) ─────────────────────────────────────────────

public sealed class DiscordEmbedTests
{
    [Fact]
    public void Embed_properties_round_trip()
    {
        var embed = new DiscordEmbed
        {
            Title = "Title",
            Description = "Description",
            Fields =
            [
                new DiscordEmbedField { Name = "f1", Value = "v1", Inline = true },
                new DiscordEmbedField { Name = "f2", Value = "v2" },
            ]
        };

        Assert.Equal("Title", embed.Title);
        Assert.Equal("Description", embed.Description);
        Assert.NotNull(embed.Fields);
        Assert.Equal(2, embed.Fields!.Count);
        Assert.Equal("f1", embed.Fields[0].Name);
        Assert.Equal("v1", embed.Fields[0].Value);
        Assert.True(embed.Fields[0].Inline);
        Assert.False(embed.Fields[1].Inline);
    }
}

public sealed class DiscordAllowedMentionsTests
{
    [Fact]
    public void None_suppresses_all_mentions()
    {
        var mentions = DiscordAllowedMentions.None;
        Assert.NotNull(mentions.Parse);
        Assert.Empty(mentions.Parse!);
    }

    [Fact]
    public void All_allows_every_mention_type()
    {
        var mentions = DiscordAllowedMentions.All;
        Assert.NotNull(mentions.Parse);
        Assert.Equal(new[] { "roles", "users", "everyone" }, mentions.Parse!);
    }
}

// ── Discord limit validation regression (offline — short-circuits before network) ──

public sealed class DiscordWebhookValidationTests
{
    private static DiscordWebhookService NewService() =>
        new(new DiscordConfig { DefaultWebhookUrl = "https://discord.com/api/webhooks/1/abc" });

    [Fact]
    public async Task SendMessage_fails_when_content_exceeds_2000_chars()
    {
        using var service = NewService();

        // 2001 chars > Discord's 2000 limit — validation rejects this before any HTTP call.
        var result = await service.SendMessageAsync(new string('x', 2001));

        Assert.True(result.IsFailure);
    }

    [Fact]
    public async Task SendEmbed_fails_when_more_than_ten_embeds()
    {
        using var service = NewService();

        var embeds = Enumerable.Range(0, 11)
            .Select(i => new DiscordEmbed { Title = $"embed {i}" })
            .ToList();

        // 11 embeds > Discord's 10 limit — validation rejects this before any HTTP call.
        var result = await service.SendEmbedAsync(embeds);

        Assert.True(result.IsFailure);
    }
}

// ── Gated live Steam test ─────────────────────────────────────────────────────────
// Boots the real CodeLogic runtime (process-wide singleton), so it lives in its own
// class behind the shared "codelogic" collection. It SKIPS unless CL_STEAM_TEST_APIKEY
// is provided.

/// <summary>
/// A <see cref="FactAttribute"/> that statically skips the test unless the named
/// environment variable is set. xUnit 2.9.3 has no runtime <c>Assert.Skip</c>, so the
/// skip decision is made here (at discovery time) and reported as a proper "Skipped".
/// </summary>
internal sealed class FactRequiresEnvAttribute : FactAttribute
{
    public FactRequiresEnvAttribute(string envVar, string reason)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envVar)))
            Skip = reason;
    }
}

public sealed class SocialRuntimeFixture : IAsyncLifetime
{
    public string TempDir { get; } =
        Path.Combine(Path.GetTempPath(), "cl_social_test_" + Guid.NewGuid().ToString("N"));

    public SocialConnectLibrary? Library { get; private set; }

    public bool Booted { get; private set; }

    public async Task InitializeAsync()
    {
        // Only boot the runtime when the live Steam test is actually configured to run.
        var apiKey = Environment.GetEnvironmentVariable("CL_STEAM_TEST_APIKEY");
        if (string.IsNullOrEmpty(apiKey))
            return;

        Directory.CreateDirectory(TempDir);

        var init = await CodeLogic.CodeLogic.InitializeAsync(o =>
        {
            o.FrameworkRootPath = TempDir;
            o.AppVersion = "1.0.0";
            o.HandleShutdownSignals = false;
        });
        if (!init.Success)
            throw new InvalidOperationException($"CodeLogic init failed: {init.Message}");

        var cfgDir = Path.Combine(TempDir, "Libraries", "CL.SocialConnect");
        Directory.CreateDirectory(cfgDir);
        File.WriteAllText(Path.Combine(cfgDir, "config.socialconnect.json"), $$"""
        {
          "enabled": true,
          "discord": {
            "enabled": false
          },
          "steam": {
            "enabled": true,
            "apiKey": "{{apiKey}}",
            "cacheTtlSeconds": 300
          }
        }
        """);

        await Libraries.LoadAsync<SocialConnectLibrary>();
        await CodeLogic.CodeLogic.ConfigureAsync();
        await CodeLogic.CodeLogic.StartAsync();

        Library = Libraries.Get<SocialConnectLibrary>()
            ?? throw new InvalidOperationException("SocialConnectLibrary not available after start.");
        Booted = true;
    }

    public async Task DisposeAsync()
    {
        if (Booted)
        {
            try { await CodeLogic.CodeLogic.StopAsync(); } catch { /* best effort */ }
        }
        try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, recursive: true); }
        catch { /* ignore lingering files on Windows */ }
    }
}

[CollectionDefinition("codelogic")]
public sealed class CodeLogicCollection : ICollectionFixture<SocialRuntimeFixture> { }

[Collection("codelogic")]
public sealed class LiveSteamTests
{
    private readonly SocialRuntimeFixture _fx;

    public LiveSteamTests(SocialRuntimeFixture fx) => _fx = fx;

    [FactRequiresEnv("CL_STEAM_TEST_APIKEY", "set CL_STEAM_TEST_APIKEY to run the live Steam test")]
    public async Task GetPlayer_returns_a_profile_for_a_known_public_steamid()
    {
        var lib = _fx.Library ?? throw new InvalidOperationException("Runtime not booted.");

        // Robin Walker — a well-known, always-public Steam profile.
        const string steamId = "76561197960435530";

        var result = await lib.Steam.GetPlayerAsync(steamId);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        Assert.Equal(steamId, result.Value!.SteamId);
    }
}
