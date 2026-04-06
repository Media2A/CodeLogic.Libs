using System.Text.Json;
using CodeLogic.Core.Logging;

namespace CL.SQLite.Services;

/// <summary>
/// Tracks applied schema migrations using a JSON history file stored in
/// <c>{dataDirectory}/migrations/migration_history.json</c>.
/// </summary>
public sealed class MigrationTracker
{
    private readonly string _historyFilePath;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public MigrationTracker(string? dataDirectory = null, ILogger? logger = null)
    {
        _logger = logger;
        var baseDir = dataDirectory ?? AppContext.BaseDirectory;
        var migrationsDir = Path.Combine(baseDir, "migrations");
        if (!Directory.Exists(migrationsDir))
            Directory.CreateDirectory(migrationsDir);
        _historyFilePath = Path.Combine(migrationsDir, "migration_history.json");
    }

    public async Task<bool> HasMigrationBeenAppliedAsync(string migrationId, CancellationToken ct = default)
    {
        var history = await LoadHistoryAsync(ct).ConfigureAwait(false);
        return history.Any(m => m.MigrationId == migrationId);
    }

    public async Task<bool> RecordMigrationAsync(
        string migrationId,
        string? description = null,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var history = await LoadHistoryInternalAsync(ct).ConfigureAwait(false);
            if (history.Any(m => m.MigrationId == migrationId))
                return true;

            history.Add(new MigrationRecord(
                migrationId,
                description,
                DateTime.UtcNow));

            await SaveHistoryAsync(history, ct).ConfigureAwait(false);
            _logger?.Debug($"[SQLite] Migration recorded: {migrationId}");
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] RecordMigrationAsync failed: {ex.Message}", ex);
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<MigrationRecord>> GetAppliedMigrationsAsync(CancellationToken ct = default)
    {
        return await LoadHistoryAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> RemoveMigrationRecordAsync(string migrationId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var history = await LoadHistoryInternalAsync(ct).ConfigureAwait(false);
            var removed = history.RemoveAll(m => m.MigrationId == migrationId) > 0;
            if (removed)
            {
                await SaveHistoryAsync(history, ct).ConfigureAwait(false);
                _logger?.Debug($"[SQLite] Migration record removed: {migrationId}");
            }
            return removed;
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] RemoveMigrationRecordAsync failed: {ex.Message}", ex);
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<List<MigrationRecord>> LoadHistoryAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await LoadHistoryInternalAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<MigrationRecord>> LoadHistoryInternalAsync(CancellationToken ct)
    {
        if (!File.Exists(_historyFilePath))
            return [];

        try
        {
            var json = await File.ReadAllTextAsync(_historyFilePath, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<List<MigrationRecord>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private async Task SaveHistoryAsync(List<MigrationRecord> history, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_historyFilePath, json, ct).ConfigureAwait(false);
    }
}

public record MigrationRecord(
    string MigrationId,
    string? Description,
    DateTime AppliedAt);
