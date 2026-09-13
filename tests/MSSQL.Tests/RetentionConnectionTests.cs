using CL.MSSQL;
using CL.MSSQL.Models;
using CL.MSSQL.Configuration;
using CL.MSSQL.Services;
using Xunit;

namespace MSSQL.Tests;

[Table(Name = "it_retain_named", Schema = "dbo")]
[RetainDays(30, nameof(CreatedUtc))]
public sealed class RetainOnNamedConnection
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "created_utc", DataType = DataType.DateTime2, NotNull = true)] public DateTime CreatedUtc { get; set; }
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
    private readonly MSSQLRuntimeFixture _fx;
    private MSSQLLibrary Mysql => _fx.Mysql;

    public RetentionConnectionTests(MSSQLRuntimeFixture fx) => _fx = fx;

    [DbFact]
    public void A_registration_records_the_connection_it_was_made_against()
    {
        var worker = new RetentionWorker(Mysql.ConnectionManager, null, []);

        Assert.True(worker.TryRegister(typeof(RetainOnNamedConnection), "Reporting"));
        Assert.True(worker.HasWork);

        var registration = Assert.Single(worker.Registrations);
        Assert.Equal(typeof(RetainOnNamedConnection), registration.Type);
        Assert.Equal("Reporting", registration.ConnectionId);
    }

    /// <summary>
    /// The same entity synced against two connections is two registrations, not one — both
    /// tables hold their own rows and both need purging.
    /// </summary>
    [DbFact]
    public void The_same_entity_on_two_connections_is_two_registrations()
    {
        var worker = new RetentionWorker(Mysql.ConnectionManager, null, []);

        Assert.True(worker.TryRegister(typeof(RetainOnNamedConnection), "Default"));
        Assert.True(worker.TryRegister(typeof(RetainOnNamedConnection), "Reporting"));
        Assert.False(worker.TryRegister(typeof(RetainOnNamedConnection), "Reporting"));

        Assert.Equal(2, worker.Registrations.Count);
        Assert.Single(worker.Entities);          // still one distinct entity type
    }

    /// <summary>
    /// The end-to-end proof. The named connection points at a <b>different database</b>,
    /// where the table exists; the default database has no such table. Before the fix the
    /// worker purged against "Default", so the DELETE hit a database without the table and
    /// the rows survived. Pointing both ids at the same database would make this pass either
    /// way and prove nothing.
    /// </summary>
    [DbFact]
    public async Task A_purge_runs_against_the_registered_connection_not_the_default()
    {
        const string named = "RetentionProbe";
        // tempdb rather than a freshly created database: CREATE DATABASE fails outright
        // on a host whose SQL Server data directory is NTFS-compressed, and the point
        // here is only that the purge targets a DIFFERENT database than "Default".
        const string otherDb = "tempdb";

        var baseCfg = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(DbEnv.ConnectionString)
        {
            InitialCatalog = otherDb,
        };
        Mysql.ConnectionManager.RegisterConfiguration(
            new SqlServerDatabaseConfig { ConnectionString = baseCfg.ConnectionString }, named);

        // The table exists only in the other database.
        await Mysql.ExecuteSqlAsync("DROP TABLE IF EXISTS [dbo].[it_retain_named]");
        await Mysql.ExecuteSqlAsync("DROP TABLE IF EXISTS [dbo].[it_retain_named]", connectionId: named);
        var synced = await Mysql.SyncTableAsync<RetainOnNamedConnection>(
            createBackup: false, connectionId: named);
        Assert.True(synced.IsSuccess, synced.Error?.ToString());

        var repo = Mysql.GetRepository<RetainOnNamedConnection>(named);
        await repo.InsertManyAsync([
            new RetainOnNamedConnection { CreatedUtc = DateTime.UtcNow.AddDays(-100) },
            new RetainOnNamedConnection { CreatedUtc = DateTime.UtcNow.AddDays(-1) },
        ]);

        await using var worker = new RetentionWorker(Mysql.ConnectionManager, null, []);
        Assert.True(worker.TryRegister(typeof(RetainOnNamedConnection), named));

        Assert.Equal(1, await worker.RunOnceAsync());
        Assert.Single((await repo.GetAllAsync()).Value!);
    }
    /// <summary>
    /// The constructor overload that takes bare types keeps its old meaning: everything is
    /// registered against the worker's own connection id, so existing callers are unchanged.
    /// </summary>
    [DbFact]
    public void The_type_only_constructor_still_uses_the_workers_connection()
    {
        var worker = new RetentionWorker(
            Mysql.ConnectionManager, null, [typeof(RetainOnNamedConnection)], "Archive");

        var registration = Assert.Single(worker.Registrations);
        Assert.Equal("Archive", registration.ConnectionId);
    }
}
