using System.Reflection;
using CL.MySQL2.Core;
using CL.MySQL2.Models;
using CodeLogic;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;

namespace CL.MySQL2.Services;

/// <summary>
/// Discovers, orders, and runs <see cref="IMigration"/> instances on top of the
/// <see cref="MigrationTracker"/>'s <c>__migrations</c> history. Migrations are applied in
/// <see cref="MigrationVersion"/> order, each in its own transaction, gated by the app version and
/// serialized across nodes by the shared <see cref="SchemaSyncLock"/>.
/// </summary>
public sealed class MigrationRunner
{
    private readonly ConnectionManager _connectionManager;
    private readonly MigrationTracker _tracker;
    private readonly SchemaAnalyzer _analyzer;
    private readonly ILogger? _logger;
    private readonly string _appVersion;
    private readonly List<IMigration> _migrations = [];

    public MigrationRunner(
        ConnectionManager connectionManager,
        MigrationTracker tracker,
        ILogger? logger = null)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _analyzer = new SchemaAnalyzer(logger);
        _logger = logger;
        _appVersion = CodeLogicEnvironment.AppVersion;
    }

    /// <summary>Registers a migration instance for discovery.</summary>
    public MigrationRunner Register(IMigration migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        _migrations.Add(migration);
        return this;
    }

    /// <summary>
    /// Registers every concrete <see cref="IMigration"/> with a public parameterless constructor
    /// found in <paramref name="assembly"/>.
    /// </summary>
    public MigrationRunner RegisterFrom(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface) continue;
            if (!typeof(IMigration).IsAssignableFrom(type)) continue;
            if (type.GetConstructor(Type.EmptyTypes) is null) continue;
            _migrations.Add((IMigration)Activator.CreateInstance(type)!);
        }
        return this;
    }

    /// <summary>Stable migration ID stored in <c>__migrations.MigrationId</c>.</summary>
    private static string MigrationId(IMigration m) =>
        $"{m.Version.AppVersion}/{m.Version.Order:D3}_{m.GetType().Name}";

    private static string Checksum(IMigration m) =>
        SchemaAnalyzer.ComputeCrc($"{MigrationId(m)}|{m.Description}");

    /// <summary>
    /// Returns the migrations that are pending (registered, at or below the current app version,
    /// and not yet applied) in apply order — without applying anything.
    /// </summary>
    public async Task<IReadOnlyList<MigrationPlanItem>> GetPendingAsync(
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        await _tracker.EnsureMigrationsTableAsync(connectionId, ct).ConfigureAwait(false);
        var applied = (await _tracker.GetAppliedMigrationsAsync(connectionId, ct).ConfigureAwait(false))
            .Select(r => r.MigrationId)
            .ToHashSet(StringComparer.Ordinal);

        return _migrations
            .Where(m => m.Version.IsAtOrBelow(_appVersion) && !applied.Contains(MigrationId(m)))
            .OrderBy(m => m.Version)
            .Select(m => new MigrationPlanItem(MigrationId(m), m.Version, m.Description))
            .ToList();
    }

    /// <summary>
    /// Applies all pending migrations in order, each in its own transaction, under the shared
    /// schema-sync lock. Also warns when a previously-applied migration's checksum has drifted.
    /// </summary>
    public async Task<Result<MigrationRunResult>> MigrateAsync(
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        await _tracker.EnsureMigrationsTableAsync(connectionId, ct).ConfigureAwait(false);

        // Checksum drift detection against the already-applied set.
        var appliedRecords = await _tracker.GetAppliedMigrationsAsync(connectionId, ct).ConfigureAwait(false);
        var appliedById = appliedRecords.ToDictionary(r => r.MigrationId, r => r, StringComparer.Ordinal);
        foreach (var m in _migrations)
        {
            if (appliedById.TryGetValue(MigrationId(m), out var rec)
                && rec.Checksum is not null
                && rec.Checksum != Checksum(m))
            {
                _logger?.Warning(
                    $"[MySQL2] Migration '{MigrationId(m)}' checksum changed since it was applied — its body may have been edited.");
            }
        }

        var pending = _migrations
            .Where(m => m.Version.IsAtOrBelow(_appVersion) && !appliedById.ContainsKey(MigrationId(m)))
            .OrderBy(m => m.Version)
            .ToList();

        if (pending.Count == 0)
        {
            _logger?.Debug("[MySQL2] No pending migrations.");
            return Result<MigrationRunResult>.Success(new MigrationRunResult([], 0));
        }

        await using var passLock = await SchemaSyncLock.AcquireAsync(
            _connectionManager, connectionId, logger: _logger, ct: ct).ConfigureAwait(false);
        if (!passLock.Acquired)
        {
            _logger?.Warning("[MySQL2] Migration pass skipped — another node holds the schema-sync lock.");
            return Result<MigrationRunResult>.Success(new MigrationRunResult([], 0));
        }

        // Re-read applied set under the lock — a peer may have just finished.
        var appliedNow = (await _tracker.GetAppliedMigrationsAsync(connectionId, ct).ConfigureAwait(false))
            .Select(r => r.MigrationId)
            .ToHashSet(StringComparer.Ordinal);

        var appliedThisRun = new List<string>();
        foreach (var m in pending)
        {
            var id = MigrationId(m);
            if (appliedNow.Contains(id)) continue;

            try
            {
                await using (var scope = await BeginScopeAsync(connectionId, ct).ConfigureAwait(false))
                {
                    var ctx = new MigrationContext(scope, _analyzer, _logger);
                    await m.UpAsync(ctx, ct).ConfigureAwait(false);
                    await scope.CommitAsync(ct).ConfigureAwait(false);
                }

                await _tracker.RecordMigrationAsync(id, m.Description, Checksum(m), connectionId, ct)
                    .ConfigureAwait(false);
                appliedThisRun.Add(id);
                _logger?.Info($"[MySQL2] Applied migration '{id}': {m.Description}");
            }
            catch (Exception ex)
            {
                _logger?.Error($"[MySQL2] Migration '{id}' failed: {ex.Message}", ex);
                return Result<MigrationRunResult>.Failure(
                    Error.Internal("mysql.migration_failed", $"Migration '{id}' failed", ex.Message));
            }
        }

        return Result<MigrationRunResult>.Success(
            new MigrationRunResult(appliedThisRun, appliedThisRun.Count));
    }

    /// <summary>
    /// Rolls back every applied migration whose version is strictly greater than
    /// <paramref name="target"/>, newest-first, each in its own transaction. Aborts before making
    /// any change if a migration in range does not support <c>DownAsync</c>.
    /// </summary>
    public async Task<Result<MigrationRunResult>> RollbackAsync(
        MigrationVersion target,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        await _tracker.EnsureMigrationsTableAsync(connectionId, ct).ConfigureAwait(false);
        var applied = await _tracker.GetAppliedMigrationsAsync(connectionId, ct).ConfigureAwait(false);
        var byId = _migrations.ToDictionary(MigrationId, m => m, StringComparer.Ordinal);

        // Determine which applied migrations are in range, newest-first.
        var toRollback = applied
            .Where(r => byId.TryGetValue(r.MigrationId, out var m) && m.Version.CompareTo(target) > 0)
            .Select(r => byId[r.MigrationId])
            .OrderByDescending(m => m.Version)
            .ToList();

        if (toRollback.Count == 0)
            return Result<MigrationRunResult>.Success(new MigrationRunResult([], 0));

        // Pre-flight: every migration in range must support rollback, else abort with no changes.
        var unsupported = toRollback.Where(m => !IsDownSupported(m)).Select(MigrationId).ToList();
        if (unsupported.Count > 0)
        {
            return Result<MigrationRunResult>.Failure(Error.Internal(
                "mysql.rollback_unsupported",
                "Rollback aborted — one or more migrations do not support DownAsync.",
                string.Join(", ", unsupported)));
        }

        await using var passLock = await SchemaSyncLock.AcquireAsync(
            _connectionManager, connectionId, logger: _logger, ct: ct).ConfigureAwait(false);
        if (!passLock.Acquired)
        {
            return Result<MigrationRunResult>.Failure(Error.Internal(
                "mysql.rollback_locked", "Rollback skipped — another node holds the schema-sync lock."));
        }

        var rolledBack = new List<string>();
        foreach (var m in toRollback)
        {
            var id = MigrationId(m);
            try
            {
                await using (var scope = await BeginScopeAsync(connectionId, ct).ConfigureAwait(false))
                {
                    var ctx = new MigrationContext(scope, _analyzer, _logger);
                    await m.DownAsync(ctx, ct).ConfigureAwait(false);
                    await scope.CommitAsync(ct).ConfigureAwait(false);
                }

                await _tracker.RemoveMigrationRecordAsync(id, connectionId, ct).ConfigureAwait(false);
                rolledBack.Add(id);
                _logger?.Info($"[MySQL2] Rolled back migration '{id}'");
            }
            catch (Exception ex)
            {
                _logger?.Error($"[MySQL2] Rollback of '{id}' failed: {ex.Message}", ex);
                return Result<MigrationRunResult>.Failure(
                    Error.Internal("mysql.rollback_failed", $"Rollback of '{id}' failed", ex.Message));
            }
        }

        return Result<MigrationRunResult>.Success(new MigrationRunResult(rolledBack, rolledBack.Count));
    }

    private static bool IsDownSupported(IMigration m)
    {
        // The Migration base supplies a DownAsync that throws. If a subclass hasn't overridden it,
        // the method's declaring type is still the base — treat that as "no rollback".
        if (m is not Migration) return true;
        var method = m.GetType().GetMethod(nameof(IMigration.DownAsync), BindingFlags.Public | BindingFlags.Instance);
        return method?.DeclaringType != typeof(Migration);
    }

    private async Task<TransactionScope> BeginScopeAsync(string connectionId, CancellationToken ct)
    {
        var conn = await _connectionManager.OpenConnectionAsync(connectionId, ct).ConfigureAwait(false);
        var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        return new TransactionScope(connectionId, conn, tx, _logger);
    }
}

/// <summary>A pending migration in the plan returned by <see cref="MigrationRunner.GetPendingAsync"/>.</summary>
public record MigrationPlanItem(string MigrationId, MigrationVersion Version, string Description);

/// <summary>Outcome of a migrate or rollback pass.</summary>
public record MigrationRunResult(IReadOnlyList<string> MigrationIds, int Count);
