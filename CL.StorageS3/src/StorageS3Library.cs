using CL.StorageS3.Models;
using CL.StorageS3.Services;
using CodeLogic.Framework.Libraries;

namespace CL.StorageS3;

/// <summary>
/// <b>CL.StorageS3</b> — CodeLogic library providing Amazon S3 and S3-compatible (MinIO, etc.)
/// object storage operations.
/// <para>
/// Lifecycle:
/// <list type="number">
///   <item><description><b>Configure</b> — registers <see cref="StorageS3Config"/>.</description></item>
///   <item><description><b>Initialize</b> — loads and validates config, registers all enabled connections in <see cref="ConnectionManager"/>.</description></item>
///   <item><description><b>Start</b> — no-op.</description></item>
///   <item><description><b>Stop</b> — disposes <see cref="ConnectionManager"/> and all cached S3 clients.</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class StorageS3Library : ILibrary
{
    /// <inheritdoc/>
    public LibraryManifest Manifest { get; } = new()
    {
        Id = "CL.StorageS3",
        Name = "Storage S3 Library",
        Version = "3.0.0",
        Description = "Amazon S3 and S3-compatible object storage for CodeLogic3",
        Author = "Media2A",
        Tags = ["storage", "s3", "aws", "minio", "objects"]
    };

    private LibraryContext? _context;
    private S3ConnectionManager? _connectionManager;

    // ── Phase 1: Configure ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task OnConfigureAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"Configuring {Manifest.Name} v{Manifest.Version}");

        context.Configuration.Register<StorageS3Config>("storages3");

        return Task.CompletedTask;
    }

    // ── Phase 2: Initialize ───────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task OnInitializeAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"Initializing {Manifest.Name}");

        var config = context.Configuration.Get<StorageS3Config>();

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

        _connectionManager = new S3ConnectionManager(context.Logger);

        foreach (var connConfig in config.Connections)
        {
            _connectionManager.RegisterConfiguration(connConfig);
            context.Logger.Info($"Registered S3 connection '{connConfig.ConnectionId}' → {connConfig.ServiceUrl}");
        }

        context.Logger.Info($"{Manifest.Name} initialized with {config.Connections.Count} connection(s)");
        return Task.CompletedTask;
    }

    // ── Phase 3: Start ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task OnStartAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"{Manifest.Name} started");
        return Task.CompletedTask;
    }

    // ── Phase 4: Stop ─────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task OnStopAsync()
    {
        _context?.Logger.Info($"Stopping {Manifest.Name}");

        _connectionManager?.Dispose();
        _connectionManager = null;

        _context?.Logger.Info($"{Manifest.Name} stopped");
        return Task.CompletedTask;
    }

    // ── Health check ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<HealthStatus> HealthCheckAsync()
    {
        var config = _context?.Configuration.Get<StorageS3Config>();

        if (config is { Enabled: false })
            return HealthStatus.Healthy("StorageS3 library is disabled");

        if (_connectionManager is null)
            return HealthStatus.Unhealthy("Not initialized");

        var ids = _connectionManager.GetConnectionIds();
        if (ids.Count == 0)
            return HealthStatus.Unhealthy("No connections registered");

        var results = new List<(string Id, bool Ok)>();
        foreach (var id in ids)
        {
            var ok = await _connectionManager.TestConnectionAsync(id);
            results.Add((id, ok));
        }

        var failed = results.Where(r => !r.Ok).Select(r => r.Id).ToList();

        if (failed.Count == 0)
            return HealthStatus.Healthy($"All {ids.Count} S3 connection(s) operational");

        if (failed.Count == ids.Count)
            return HealthStatus.Unhealthy($"All S3 connections failed: {string.Join(", ", failed)}");

        return HealthStatus.Degraded($"S3 connections unavailable: {string.Join(", ", failed)}");
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// The underlying connection manager. Use this for low-level client access.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the library has not been initialized or is disabled.
    /// </exception>
    public S3ConnectionManager ConnectionManager =>
        _connectionManager ?? throw new InvalidOperationException("StorageS3 not initialized or disabled");

    /// <summary>
    /// Returns a new <see cref="S3StorageService"/> scoped to the given connection.
    /// </summary>
    /// <param name="connectionId">Connection ID to use. Defaults to <c>"Default"</c>.</param>
    public S3StorageService GetService(string connectionId = "Default") =>
        new(ConnectionManager, connectionId, _context?.Logger, _context?.Events);

    /// <summary>
    /// Returns a new <see cref="S3StorageService"/> scoped to the <c>"Default"</c> connection.
    /// </summary>
    public S3StorageService DefaultService => GetService();

    // ── IDisposable ───────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        _connectionManager?.Dispose();
        _connectionManager = null;
    }
}
