using System.Linq.Expressions;
using CodeLogic;                          // Libraries, CodeLogicOptions
using CL.PostgreSQL;
using CL.PostgreSQL.Models;
using Xunit;

namespace PostgreSQL.Tests;

// ── CL.PostgreSQL tests ─────────────────────────────────────────────────────────
// HYBRID strategy:
//   • Config models (PostgreSQLConfig / DatabaseConfig) are pure, offline objects —
//     validation and connection-string building are exercised directly with no
//     external service. These ALWAYS run.
//   • Real CRUD, the query builder, and the two bug-fix regressions need a live
//     PostgreSQL server, so they live behind a single shared fixture and SKIP unless
//     CL_PG_TEST_HOST is set.

// ── OFFLINE: config validation + connection string ───────────────────────────────

public sealed class DatabaseConfigTests
{
    [Fact]
    public void Defaults_match_source()
    {
        var cfg = new DatabaseConfig();

        Assert.True(cfg.Enabled);
        Assert.Equal("localhost", cfg.Host);
        Assert.Equal(5432, cfg.Port);
        Assert.Equal(string.Empty, cfg.Database);
        Assert.Equal(string.Empty, cfg.Username);
        Assert.Equal(string.Empty, cfg.Password);
        Assert.Equal(30, cfg.ConnectionTimeout);
        Assert.Equal(30, cfg.CommandTimeout);
        Assert.Equal(5, cfg.MinPoolSize);
        Assert.Equal(100, cfg.MaxPoolSize);
        Assert.Equal(60, cfg.MaxIdleTime);
        Assert.Equal(SslMode.Prefer, cfg.SslMode);
        Assert.False(cfg.AllowDestructiveSync);
        Assert.Equal(1000, cfg.SlowQueryThresholdMs);
    }

    [Fact]
    public void Validate_fails_when_required_fields_missing()
    {
        // A default config has empty Database + Username → invalid.
        var result = new DatabaseConfig().Validate();

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Database", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, e => e.Contains("Username", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_fails_when_host_blank()
    {
        var cfg = new DatabaseConfig { Host = "  ", Database = "db", Username = "u" };
        var result = cfg.Validate();

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Host", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_passes_when_required_fields_present()
    {
        var cfg = new DatabaseConfig
        {
            Host = "localhost",
            Database = "mydb",
            Username = "postgres"
        };

        var result = cfg.Validate();

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void BuildConnectionString_contains_host_port_and_database()
    {
        var cfg = new DatabaseConfig
        {
            Host = "db.example.com",
            Port = 6543,
            Database = "widgets",
            Username = "appuser",
            Password = "secret"
        };

        var connStr = cfg.BuildConnectionString();

        Assert.Contains("Host=db.example.com", connStr);
        Assert.Contains("Port=6543", connStr);
        Assert.Contains("Database=widgets", connStr);
        Assert.Contains("Username=appuser", connStr);
        Assert.Contains($"SSL Mode={SslMode.Prefer}", connStr);
        Assert.Contains("Minimum Pool Size=5", connStr);
        Assert.Contains("Maximum Pool Size=100", connStr);
    }
}

public sealed class PostgreSQLConfigTests
{
    [Fact]
    public void Default_config_has_one_Default_database()
    {
        var cfg = new PostgreSQLConfig();

        Assert.True(cfg.Databases.ContainsKey("Default"));
        Assert.Single(cfg.Databases);
    }

    [Fact]
    public void Validate_aggregates_database_errors()
    {
        // The default "Default" database has empty Database/Username → top-level invalid.
        var cfg = new PostgreSQLConfig();
        var result = cfg.Validate();

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Default", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_passes_for_fully_specified_database()
    {
        var cfg = new PostgreSQLConfig
        {
            Databases = new()
            {
                ["Default"] = new DatabaseConfig
                {
                    Host = "localhost",
                    Database = "mydb",
                    Username = "postgres"
                }
            }
        };

        var result = cfg.Validate();

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_fails_when_no_databases()
    {
        var cfg = new PostgreSQLConfig { Databases = new() };
        var result = cfg.Validate();

        Assert.False(result.IsValid);
    }
}

// ── Gated integration: test entity ───────────────────────────────────────────────

[Table(Name = "it_pg_widget")]
public sealed class PgWidget
{
    [Column(Name = "id", DataType = DataType.Int, Primary = true, AutoIncrement = true)]
    public int Id { get; set; }

    [Column(Name = "name", DataType = DataType.VarChar, Size = 100, NotNull = true)]
    public string Name { get; set; } = "";

    [Column(Name = "quantity", DataType = DataType.Int, NotNull = true)]
    public int Quantity { get; set; }

    [Column(Name = "active", DataType = DataType.Bool, NotNull = true)]
    public bool Active { get; set; }

    // Nullable column for the null-comparison regression.
    [Column(Name = "note", DataType = DataType.VarChar, Size = 200)]
    public string? Note { get; set; }
}

// ── Gated env attribute (mirrors tests/Mail.Tests/MailTests.cs) ───────────────────

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

// ── Shared one-time-boot fixture ─────────────────────────────────────────────────
// CodeLogic is a process-wide singleton, so the booted library lives behind a single
// shared fixture that boots once — but ONLY when CL_PG_TEST_HOST is set. When it is
// unset the fixture does nothing and all gated tests skip at discovery time.

public sealed class PostgreSQLRuntimeFixture : IAsyncLifetime
{
    public string TempDir { get; } =
        Path.Combine(Path.GetTempPath(), "cl_pg_test_" + Guid.NewGuid().ToString("N"));

    public PostgreSQLLibrary? Library { get; private set; }

    public bool Booted { get; private set; }

    public async Task InitializeAsync()
    {
        var host = Environment.GetEnvironmentVariable("CL_PG_TEST_HOST");
        if (string.IsNullOrEmpty(host))
            return; // gated tests will skip; do not boot the runtime

        var port = Environment.GetEnvironmentVariable("CL_PG_TEST_PORT") ?? "5432";
        var db   = Environment.GetEnvironmentVariable("CL_PG_TEST_DB")   ?? "postgres";
        var user = Environment.GetEnvironmentVariable("CL_PG_TEST_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("CL_PG_TEST_PASS") ?? "";

        Directory.CreateDirectory(TempDir);

        var init = await CodeLogic.CodeLogic.InitializeAsync(o =>
        {
            o.FrameworkRootPath = TempDir;
            o.AppVersion = "1.0.0";
            o.HandleShutdownSignals = false;
        });
        if (!init.Success)
            throw new InvalidOperationException($"CodeLogic init failed: {init.Message}");

        var cfgDir = Path.Combine(TempDir, "Libraries", "CL.PostgreSQL");
        Directory.CreateDirectory(cfgDir);
        File.WriteAllText(Path.Combine(cfgDir, "config.postgresql.json"), $$"""
        {
          "databases": {
            "Default": {
              "enabled": true,
              "host": "{{host}}",
              "port": {{port}},
              "database": "{{db}}",
              "username": "{{user}}",
              "password": "{{pass}}",
              "allowDestructiveSync": true
            }
          }
        }
        """);

        await Libraries.LoadAsync<PostgreSQLLibrary>();
        await CodeLogic.CodeLogic.ConfigureAsync();
        await CodeLogic.CodeLogic.StartAsync();

        Library = Libraries.Get<PostgreSQLLibrary>()
            ?? throw new InvalidOperationException("PostgreSQLLibrary not available after start.");
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
public sealed class CodeLogicCollection : ICollectionFixture<PostgreSQLRuntimeFixture> { }

// ── Gated integration tests ──────────────────────────────────────────────────────

[Collection("codelogic")]
public sealed class PostgreSQLIntegrationTests
{
    private const string Gate = "CL_PG_TEST_HOST";
    private const string Reason = "set CL_PG_TEST_HOST (+ _PORT/_DB/_USER/_PASS) to run live PostgreSQL tests";

    private readonly PostgreSQLRuntimeFixture _fx;
    private PostgreSQLLibrary Lib => _fx.Library
        ?? throw new InvalidOperationException("Runtime not booted.");

    public PostgreSQLIntegrationTests(PostgreSQLRuntimeFixture fx) => _fx = fx;

    private async Task<PostgreSQLLibrary> FreshTableAsync()
    {
        var lib = Lib;
        // Clean slate each test so row-count assertions are deterministic.
        await lib.QueryRaw().ExecuteAsync("DROP TABLE IF EXISTS \"public\".\"it_pg_widget\"");
        var sync = await lib.SyncTableAsync<PgWidget>(createBackup: false);
        Assert.True(sync.IsSuccess, sync.Error?.Message);
        Assert.True(sync.Value!.Success, string.Join("; ", sync.Value.Errors));
        return lib;
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task SyncTable_succeeds()
    {
        var sync = await Lib.SyncTableAsync<PgWidget>(createBackup: false);
        Assert.True(sync.IsSuccess, sync.Error?.Message);
        Assert.True(sync.Value!.Success, string.Join("; ", sync.Value.Errors));
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task Insert_GetById_round_trips()
    {
        var lib = await FreshTableAsync();
        var repo = lib.GetRepository<PgWidget>();

        var ins = await repo.InsertAsync(new PgWidget { Name = "alpha", Quantity = 3, Active = true });
        Assert.True(ins.IsSuccess, ins.Error?.Message);
        Assert.True(ins.Value!.Id > 0);

        var fetched = await repo.GetByIdAsync(ins.Value.Id);
        Assert.True(fetched.IsSuccess, fetched.Error?.Message);
        Assert.NotNull(fetched.Value);
        Assert.Equal("alpha", fetched.Value!.Name);
        Assert.Equal(3, fetched.Value.Quantity);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task GetAll_sees_inserted_rows()
    {
        var lib = await FreshTableAsync();
        var repo = lib.GetRepository<PgWidget>();

        var a = (await repo.InsertAsync(new PgWidget { Name = "all-a", Quantity = 1, Active = true })).Value!.Id;
        var b = (await repo.InsertAsync(new PgWidget { Name = "all-b", Quantity = 2, Active = true })).Value!.Id;

        var all = await repo.GetAllAsync();
        Assert.True(all.IsSuccess, all.Error?.Message);
        Assert.Contains(all.Value!, w => w.Id == a);
        Assert.Contains(all.Value!, w => w.Id == b);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task QueryBuilder_Where_filters_and_Count_matches()
    {
        var lib = await FreshTableAsync();
        var repo = lib.GetRepository<PgWidget>();
        await repo.InsertAsync(new PgWidget { Name = "qb", Quantity = 10, Active = true });
        await repo.InsertAsync(new PgWidget { Name = "qb", Quantity = 20, Active = true });
        await repo.InsertAsync(new PgWidget { Name = "qb", Quantity = 99, Active = true });

        var filtered = await lib.Query<PgWidget>()
            .Where(w => w.Name == "qb" && w.Quantity < 50)
            .ToListAsync();
        Assert.True(filtered.IsSuccess, filtered.Error?.Message);
        Assert.Equal(2, filtered.Value!.Count);
        Assert.All(filtered.Value!, w => Assert.True(w.Quantity < 50));

        var count = await lib.Query<PgWidget>().Where(w => w.Name == "qb").CountAsync();
        Assert.True(count.IsSuccess, count.Error?.Message);
        Assert.Equal(3L, count.Value);
    }

    // ── Regression: 11+ parameters in one predicate (param re-key) ────────────────
    // A predicate emitting 11+ parameters used to corrupt the SQL because the re-key
    // loop replaced "@p1" inside "@p10"/"@p11" (substring collision). The fix replaces
    // the longest parameter names first. Build a 12-term OR chain (params @p0…@p11)
    // and confirm it executes and matches exactly the intended rows.
    [FactRequiresEnv(Gate, Reason)]
    public async Task Where_with_twelve_params_does_not_corrupt_sql()
    {
        var lib = await FreshTableAsync();
        var repo = lib.GetRepository<PgWidget>();

        var ids = new List<int>();
        for (var i = 0; i < 12; i++)
            ids.Add((await repo.InsertAsync(new PgWidget { Name = "many", Quantity = i, Active = true })).Value!.Id);

        // Build (Id == ids[0] || Id == ids[1] || … || Id == ids[11]) as an expression tree.
        var p = Expression.Parameter(typeof(PgWidget), "w");
        var idProp = Expression.Property(p, nameof(PgWidget.Id));
        Expression body = Expression.Equal(idProp, Expression.Constant(ids[0]));
        for (var i = 1; i < ids.Count; i++)
            body = Expression.OrElse(body, Expression.Equal(idProp, Expression.Constant(ids[i])));
        var predicate = Expression.Lambda<Func<PgWidget, bool>>(body, p);

        var result = await lib.Query<PgWidget>().Where(predicate).ToListAsync();

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(12, result.Value!.Count);
        Assert.Equal(ids.OrderBy(x => x), result.Value!.Select(w => w.Id).OrderBy(x => x));
    }

    // ── Regression: null comparison ANDed with another clause ─────────────────────
    // A compound predicate that ANDs a clause with a "== null" check used to wipe the
    // SQL buffer in the null branch, dropping the preceding clauses. Insert rows with
    // null and non-null notes and assert the filter returns only the active null-note
    // rows.
    [FactRequiresEnv(Gate, Reason)]
    public async Task Where_active_and_note_is_null_filters_correctly()
    {
        var lib = await FreshTableAsync();
        var repo = lib.GetRepository<PgWidget>();

        // active + null note  → MATCH
        await repo.InsertAsync(new PgWidget { Name = "n1", Quantity = 1, Active = true, Note = null });
        // active + non-null note → no match (note set)
        await repo.InsertAsync(new PgWidget { Name = "n2", Quantity = 2, Active = true, Note = "has-note" });
        // inactive + null note → no match (not active)
        await repo.InsertAsync(new PgWidget { Name = "n3", Quantity = 3, Active = false, Note = null });
        // another active + null note → MATCH
        await repo.InsertAsync(new PgWidget { Name = "n4", Quantity = 4, Active = true, Note = null });

        var result = await lib.Query<PgWidget>()
            .Where(w => w.Active && w.Note == null)
            .ToListAsync();

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(2, result.Value!.Count);
        Assert.All(result.Value!, w => Assert.True(w.Active && w.Note == null));
        Assert.Equal(new[] { "n1", "n4" }, result.Value!.Select(w => w.Name).OrderBy(x => x));
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task HealthCheck_is_healthy()
    {
        await FreshTableAsync();
        var health = await Lib.HealthCheckAsync();
        Assert.Equal(CodeLogic.Framework.Libraries.HealthStatusLevel.Healthy, health.Status);
    }
}
