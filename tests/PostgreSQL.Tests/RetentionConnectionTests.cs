using CL.PostgreSQL;
using CL.PostgreSQL.Models;
using CL.PostgreSQL.Configuration;
using CL.PostgreSQL.Services;
using Xunit;

namespace PostgreSQL.Tests;

[Table(Name = "it_retain_named")]
[RetainDays(30, nameof(CreatedUtc))]
public sealed class RetainOnNamedConnection
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "created_utc", NotNull = true)] public DateTime CreatedUtc { get; set; }
}

/// <summary>
/// The retention worker used to purge everything against <c>"Default"</c>, because the
/// library's registered-entity set recorded types with no connection association. An entity
/// synced against a named connection was therefore purged from the wrong database — in
/// practice the DELETE hit a database where the table did not exist, so the retention the
/// attribute described silently never happened.
/// <para>
/// This only became reachable once the worker actually started, which itself was fixed in
/// the same release, so it shipped unnoticed.
/// </para>
/// </summary>
[Collection("codelogic")]
public sealed class RetentionConnectionTests
{
    private readonly PostgreSQLRuntimeFixture _fx;
    private PostgreSQLLibrary Mysql => _fx.Library ?? throw new InvalidOperationException("Runtime not booted.");

    private const string Gate = "CL_PG_TEST_HOST";
    private const string Reason = "set CL_PG_TEST_HOST (+ _PORT/_DB/_USER/_PASS) to run live PostgreSQL tests";

    public RetentionConnectionTests(PostgreSQLRuntimeFixture fx) => _fx = fx;

    [FactRequiresEnv(Gate, Reason)]
    public void A_registration_records_the_connection_it_was_made_against()
    {
        var worker = new RetentionWorker(Mysql.ConnectionManager, null, []);

        Assert.True(worker.Register(typeof(RetainOnNamedConnection), "Reporting"));
        Assert.True(worker.HasWork);

        var registration = Assert.Single(worker.Registrations);
        Assert.Equal(typeof(RetainOnNamedConnection), registration.Type);
        Assert.Equal("Reporting", registration.ConnectionId);
    }

    /// <summary>
    /// The same entity synced against two connections is two registrations, not one — both
    /// tables hold their own rows and both need purging.
    /// </summary>
    [FactRequiresEnv(Gate, Reason)]
    public void The_same_entity_on_two_connections_is_two_registrations()
    {
        var worker = new RetentionWorker(Mysql.ConnectionManager, null, []);

        Assert.True(worker.Register(typeof(RetainOnNamedConnection), "Default"));
        Assert.True(worker.Register(typeof(RetainOnNamedConnection), "Reporting"));
        Assert.False(worker.Register(typeof(RetainOnNamedConnection), "Reporting"));

        Assert.Equal(2, worker.Registrations.Count);
        // PostgreSQL's worker exposes Registrations only; distinctness is the point.
        Assert.Single(worker.Registrations.Select(r => r.Type).Distinct());
    }

    /// <summary>
    /// The end-to-end proof. The named connection points at a <b>different database</b>,
    /// where the table exists; the default database has no such table. Before the fix the
    /// worker purged against "Default", so the DELETE hit a database without the table and
    /// the rows survived. Pointing both ids at the same database would make this pass either
    /// way and prove nothing.
    /// </summary>
    [FactRequiresEnv(Gate, Reason)]
    public async Task A_purge_runs_against_the_registered_connection_not_the_default()
    {
        const string named = "RetentionProbe";
        // "postgres" is the always-present maintenance database on any server, so the
        // named connection genuinely points somewhere other than the default.
        const string otherDb = "postgres";

        Mysql.ConnectionManager.RegisterConfiguration(new PostgreSqlDatabaseConfig
        {
            Host = Environment.GetEnvironmentVariable("CL_PG_TEST_HOST") ?? "127.0.0.1",
            Port = int.Parse(Environment.GetEnvironmentVariable("CL_PG_TEST_PORT") ?? "5432"),
            Database = otherDb,
            Username = Environment.GetEnvironmentVariable("CL_PG_TEST_USER") ?? "postgres",
            Password = Environment.GetEnvironmentVariable("CL_PG_TEST_PASS") ?? "",
        }, named);

        // The table exists only in the other database.
        await Mysql.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_retain_named\" CASCADE");
        await Mysql.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_retain_named\" CASCADE", connectionId: named);
        Assert.True((await Mysql.SyncTableAsync<RetainOnNamedConnection>(
            createBackup: false, connectionId: named)).IsSuccess);

        var repo = Mysql.GetRepository<RetainOnNamedConnection>(named);
        await repo.InsertManyAsync([
            new RetainOnNamedConnection { CreatedUtc = DateTime.UtcNow.AddDays(-100) },
            new RetainOnNamedConnection { CreatedUtc = DateTime.UtcNow.AddDays(-1) },
        ]);

        await using var worker = new RetentionWorker(Mysql.ConnectionManager, null, []);
        Assert.True(worker.Register(typeof(RetainOnNamedConnection), named));

        Assert.Equal(1, await worker.RunOnceAsync());
        Assert.Single((await repo.GetAllAsync()).Value!);
    }
    /// <summary>
    /// The constructor overload that takes bare types keeps its old meaning: everything is
    /// registered against the worker's own connection id, so existing callers are unchanged.
    /// </summary>
    [FactRequiresEnv(Gate, Reason)]
    public void The_type_only_constructor_still_uses_the_workers_connection()
    {
        var worker = new RetentionWorker(
            Mysql.ConnectionManager, null, [typeof(RetainOnNamedConnection)], "Archive");

        var registration = Assert.Single(worker.Registrations);
        Assert.Equal("Archive", registration.ConnectionId);
    }
}
