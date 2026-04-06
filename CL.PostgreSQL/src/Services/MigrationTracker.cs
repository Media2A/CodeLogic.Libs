using System.Text.Json;
using CodeLogic.Core.Logging;

namespace CL.PostgreSQL.Services;

/// <summary>
/// Tracks applied database migrations using a JSON history file.
/// History is stored at {dataDirectory}/migrations/migration_history.json.
/// </summary>
public sealed class MigrationTracker
{
    private readonly string _dataDirectory;
    private readonly ILogger? _logger;
    private readonly string _historyFilePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public MigrationTracker(string dataDirectory, ILogger? logger = null)
    {
        _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        _logger = logger;
        _historyFilePath = Path.Combine(_dataDirectory, "migrations", "migration_history.json");
    }

    /// <summary>
    /// Ensures the migrations directory and history file exist.
    /// </summary>
    public async Task<bool> EnsureMigrationsFileAsync(CancellationToken ct = default)
    {
        try
        {
            var dir = Path.GetDirectoryName(_historyFilePath)!;
            Directory.CreateDirectory(dir);

            if (!File.Exists(_historyFilePath))
            {
                await File.WriteAllTextAsync(_historyFilePath, "[]", ct).ConfigureAwait(false);
            }

            _logger?.Debug("[PostgreSQL] Migrations history file ready");
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Error($"[PostgreSQL] Failed to ensure migrations file: {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>
    /// Returns true when the migration with the given ID has already been applied.
    /// </summary>
    public async Task<bool> HasMigrationBeenAppliedAsync(
        string migrationId,
        CancellationToken ct = default)
    {
        try
        {
            var records = await LoadRecordsAsync(ct).ConfigureAwait(false);
            return records.Any(r => string.Equals(r.MigrationId, migrationId, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger?.Error($"[PostgreSQL] HasMigrationBeenAppliedAsync failed: {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>
    /// Records a successfully applied migration.
    /// </summary>
    public async Task<bool> RecordMigrationAsync(
        string migrationId,
        string? description = null,
        string? checksum = null,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var records = await LoadRecordsAsync(ct).ConfigureAwait(false);

            if (records.Any(r => string.Equals(r.MigrationId, migrationId, StringComparison.OrdinalIgnoreCase)))
                return true; // Already recorded

            var nextId = records.Count > 0 ? records.Max(r => r.Id) + 1 : 1;
            records.Add(new MigrationRecord(nextId, migrationId, description, DateTime.UtcNow, checksum));
            await SaveRecordsAsync(records, ct).ConfigureAwait(false);

            _logger?.Info($"[PostgreSQL] Migration recorded: {migrationId}");
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Error($"[PostgreSQL] RecordMigrationAsync failed: {ex.Message}", ex);
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Returns all applied migration records ordered by application date.
    /// </summary>
    public async Task<List<MigrationRecord>> GetAppliedMigrationsAsync(CancellationToken ct = default)
    {
        try
        {
            var records = await LoadRecordsAsync(ct).ConfigureAwait(false);
            return records.OrderBy(r => r.AppliedAt).ToList();
        }
        catch (Exception ex)
        {
            _logger?.Error($"[PostgreSQL] GetAppliedMigrationsAsync failed: {ex.Message}", ex);
            return [];
        }
    }

    /// <summary>
    /// Removes a migration record (e.g., for rollback purposes).
    /// </summary>
    public async Task<bool> RemoveMigrationRecordAsync(
        string migrationId,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var records = await LoadRecordsAsync(ct).ConfigureAwait(false);
            var removed = records.RemoveAll(r =>
                string.Equals(r.MigrationId, migrationId, StringComparison.OrdinalIgnoreCase));

            if (removed > 0)
            {
                await SaveRecordsAsync(records, ct).ConfigureAwait(false);
                _logger?.Info($"[PostgreSQL] Migration record removed: {migrationId}");
            }

            return removed > 0;
        }
        catch (Exception ex)
        {
            _logger?.Error($"[PostgreSQL] RemoveMigrationRecordAsync failed: {ex.Message}", ex);
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<MigrationRecord>> LoadRecordsAsync(CancellationToken ct)
    {
        if (!File.Exists(_historyFilePath))
            return [];

        var json = await File.ReadAllTextAsync(_historyFilePath, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json)) return [];

        return JsonSerializer.Deserialize<List<MigrationRecord>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    }

    private async Task SaveRecordsAsync(List<MigrationRecord> records, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_historyFilePath, json, ct).ConfigureAwait(false);
    }
}

public record MigrationRecord(
    int Id,
    string MigrationId,
    string? Description,
    DateTime AppliedAt,
    string? Checksum);
