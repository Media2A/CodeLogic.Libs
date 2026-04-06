using CL.SocialConnect.Models;
using CL.SocialConnect.Services.Discord;
using CL.SocialConnect.Services.Steam;
using CodeLogic.Framework.Libraries;

namespace CL.SocialConnect;

/// <summary>
/// <b>CL.SocialConnect</b> — CodeLogic library providing Discord webhook messaging
/// and Steam profile/authentication services.
/// <para>
/// Lifecycle:
/// <list type="number">
///   <item><description><b>Configure</b> — registers <see cref="SocialConnectConfig"/> (<c>config.socialconnect.json</c>).</description></item>
///   <item><description><b>Initialize</b> — validates config, creates services based on enabled flags.</description></item>
///   <item><description><b>Start</b> — logs that the library is ready.</description></item>
///   <item><description><b>Stop</b> — disposes HTTP clients and clears state.</description></item>
/// </list>
/// </para>
/// <para>
/// Access services via the public properties:
/// <list type="bullet">
///   <item><see cref="Discord"/> — <see cref="DiscordWebhookService"/> for sending webhook messages.</item>
///   <item><see cref="Steam"/> — <see cref="SteamProfileService"/> for player profiles, bans, and games.</item>
///   <item><see cref="Auth"/> — <see cref="SteamAuthenticationService"/> for ticket-based Steam auth.</item>
/// </list>
/// </para>
/// </summary>
public sealed class SocialConnectLibrary : ILibrary
{
    /// <inheritdoc/>
    public LibraryManifest Manifest { get; } = new()
    {
        Id = "CL.SocialConnect",
        Name = "Social Connect Library",
        Version = "3.0.0",
        Description = "Discord webhook messaging and Steam profile/authentication services",
        Author = "Media2A",
        Tags = ["discord", "steam", "webhook", "social", "gaming"]
    };

    private LibraryContext? _context;
    private DiscordWebhookService? _discord;
    private SteamProfileService? _steam;
    private SteamAuthenticationService? _auth;

    // ── Phase 1: Configure ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task OnConfigureAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"Configuring {Manifest.Name} v{Manifest.Version}");

        context.Configuration.Register<SocialConnectConfig>("socialconnect");

        return Task.CompletedTask;
    }

    // ── Phase 2: Initialize ───────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task OnInitializeAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"Initializing {Manifest.Name}");

        var config = context.Configuration.Get<SocialConnectConfig>();

        var validation = config.Validate();
        if (!validation.IsValid)
        {
            var errors = string.Join(", ", validation.Errors);
            context.Logger.Error($"{Manifest.Name} configuration is invalid: {errors}");
            throw new InvalidOperationException($"{Manifest.Name} configuration is invalid: {errors}");
        }

        if (!config.Enabled)
        {
            context.Logger.Warning($"{Manifest.Name} is disabled in configuration — skipping initialization.");
            return Task.CompletedTask;
        }

        // Discord service
        if (config.Discord.Enabled)
        {
            _discord = new DiscordWebhookService(config.Discord, context.Logger, context.Events);
            context.Logger.Info("Discord webhook service initialized");
        }

        // Steam profile service
        if (config.Steam.Enabled)
        {
            _steam = new SteamProfileService(config.Steam, context.Logger, context.Events);
            context.Logger.Info("Steam profile service initialized");

            // Steam authentication service (optional, requires AppId)
            if (config.Steam.AuthEnabled)
            {
                _auth = new SteamAuthenticationService(config.Steam, context.Logger, context.Events);
                context.Logger.Info("Steam authentication service initialized");
            }
        }

        context.Logger.Info($"{Manifest.Name} initialized");
        return Task.CompletedTask;
    }

    // ── Phase 3: Start ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task OnStartAsync(LibraryContext context)
    {
        _context = context;

        var services = new List<string>();
        if (_discord is not null) services.Add("Discord");
        if (_steam is not null) services.Add("Steam");
        if (_auth is not null) services.Add("SteamAuth");

        context.Logger.Info(services.Count > 0
            ? $"{Manifest.Name} started — active services: {string.Join(", ", services)}"
            : $"{Manifest.Name} started (all services disabled)"
        );

        return Task.CompletedTask;
    }

    // ── Phase 4: Stop ─────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task OnStopAsync()
    {
        _context?.Logger.Info($"Stopping {Manifest.Name}");

        _discord = null;
        _steam = null;
        _auth = null;

        _context?.Logger.Info($"{Manifest.Name} stopped");
        return Task.CompletedTask;
    }

    // ── Health check ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<HealthStatus> HealthCheckAsync()
    {
        var config = _context?.Configuration.Get<SocialConnectConfig>();

        if (config is { Enabled: false })
            return Task.FromResult(HealthStatus.Healthy($"{Manifest.Name} is disabled"));

        if (_discord is null && _steam is null)
            return Task.FromResult(HealthStatus.Unhealthy("Not initialized — no services active"));

        var active = new List<string>();
        if (_discord is not null) active.Add("Discord");
        if (_steam is not null) active.Add("Steam");
        if (_auth is not null) active.Add("SteamAuth");

        return Task.FromResult(
            HealthStatus.Healthy($"Active services: {string.Join(", ", active)}")
        );
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// The Discord webhook service for sending messages and embeds.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when Discord is not initialized or disabled.
    /// </exception>
    public DiscordWebhookService Discord =>
        _discord ?? throw new InvalidOperationException("Discord service not available — check config.Discord.Enabled");

    /// <summary>
    /// The Steam profile service for fetching player profiles, ban records, and owned games.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when Steam is not initialized or disabled.
    /// </exception>
    public SteamProfileService Steam =>
        _steam ?? throw new InvalidOperationException("Steam service not available — check config.Steam.Enabled");

    /// <summary>
    /// The Steam authentication service for validating session tickets.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when Steam authentication is not initialized or disabled.
    /// </exception>
    public SteamAuthenticationService Auth =>
        _auth ?? throw new InvalidOperationException("Steam auth service not available — check config.Steam.AuthEnabled");

    /// <summary>Returns <c>true</c> when the Steam profile service is available.</summary>
    public bool HasSteam => _steam is not null;

    /// <summary>Returns <c>true</c> when the Steam authentication service is available.</summary>
    public bool HasSteamAuth => _auth is not null;

    /// <summary>Returns <c>true</c> when the Discord webhook service is available.</summary>
    public bool HasDiscord => _discord is not null;

    // ── IDisposable ───────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        _discord = null;
        _steam = null;
        _auth = null;
    }
}
