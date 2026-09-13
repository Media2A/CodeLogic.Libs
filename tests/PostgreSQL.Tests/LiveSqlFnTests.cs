using CL.PostgreSQL;
using CL.PostgreSQL.Core;
using CL.PostgreSQL.Models;
using Xunit;
using Xunit.Abstractions;

namespace PostgreSQL.Tests;

// Every SqlFn member, executed on the server. Each one is a hand-written dialect
// translation, and only six of the seventeen had ever run — the rest were carried over
// from the MySQL original and rewritten by hand for PostgreSQL.
//
// SqlFn is only translated inside a grouped projection, so each case groups by the
// expression under test and reads back the key.

[Table(Name = "it_fn", Schema = "public")]
public sealed class FnRow
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "at", NotNull = true)] public DateTime At { get; set; }
    [Column(Name = "word", Size = 40, NotNull = true)] public string Word { get; set; } = "";
    [Column(Name = "other", Size = 40)] public string? Other { get; set; }
    [Column(Name = "amount", DataType = DataType.DoublePrecision, NotNull = true)] public double Amount { get; set; }
}

[Collection("codelogic")]
public sealed class LiveSqlFnTests
{
    private const string Gate = "CL_PG_TEST_HOST";
    private const string Reason = "set CL_PG_TEST_HOST (+ _PORT/_DB/_USER/_PASS) to run live PostgreSQL tests";

    // 2026-03-14 15:09:26 UTC — a Saturday, so DayOfWeek is 6 under .NET's convention.
    private static readonly DateTime Sample = new(2026, 3, 14, 15, 9, 26, DateTimeKind.Utc);

    private readonly PostgreSQLRuntimeFixture _fx;
    private readonly ITestOutputHelper _out;
    private PostgreSQLLibrary Lib => _fx.Library ?? throw new InvalidOperationException("Runtime not booted.");

    public LiveSqlFnTests(PostgreSQLRuntimeFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _out = output;
    }

    private async Task<PostgreSQLLibrary> SeededAsync()
    {
        var lib = Lib;
        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_fn\" CASCADE");
        Assert.True((await lib.SyncTableAsync<FnRow>(createBackup: false)).IsSuccess);
        await lib.GetRepository<FnRow>().InsertAsync(new FnRow
        {
            At = Sample,
            Word = "MiXeD",
            Other = null,
            Amount = 2.345,
        });
        return lib;
    }

    /// <summary>Groups by the expression under test and returns the single key produced.</summary>
    private async Task<TKey> KeyAsync<TKey>(
        PostgreSQLLibrary lib,
        System.Linq.Expressions.Expression<Func<FnRow, TKey>> keySelector)
    {
        var rows = await lib.Query<FnRow>()
            .GroupBy(keySelector)
            .Select(g => new { K = g.Key })
            .ToListAsync();
        Assert.True(rows.IsSuccess, rows.Error?.ToString());
        Assert.Single(rows.Value!);
        return rows.Value![0].K;
    }

    // ── Date parts ───────────────────────────────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task Date_part_functions_extract_the_right_components()
    {
        var lib = await SeededAsync();

        Assert.Equal(2026, await KeyAsync(lib, r => SqlFn.Year(r.At)));
        Assert.Equal(3, await KeyAsync(lib, r => SqlFn.Month(r.At)));
        Assert.Equal(14, await KeyAsync(lib, r => SqlFn.Day(r.At)));
        Assert.Equal(15, await KeyAsync(lib, r => SqlFn.Hour(r.At)));
        Assert.Equal(9, await KeyAsync(lib, r => SqlFn.Minute(r.At)));
    }

    /// <summary>
    /// PostgreSQL's DOW is 0-6 from Sunday, matching .NET's DayOfWeek. MySQL's DAYOFWEEK is
    /// 1-7, and the MySQL library subtracts one; doing that here would be off by one.
    /// </summary>
    [FactRequiresEnv(Gate, Reason)]
    public async Task DayOfWeek_matches_the_dotnet_convention()
    {
        var lib = await SeededAsync();
        var expected = (int)Sample.DayOfWeek;           // Saturday == 6
        Assert.Equal(6, expected);
        Assert.Equal(expected, await KeyAsync(lib, r => SqlFn.DayOfWeek(r.At)));
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task Date_truncates_the_time_component()
    {
        var lib = await SeededAsync();
        var day = await KeyAsync(lib, r => SqlFn.Date(r.At));
        Assert.Equal(new DateTime(2026, 3, 14), day.Date);
        Assert.Equal(TimeSpan.Zero, day.TimeOfDay);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task BucketUtc_rounds_down_to_the_window()
    {
        var lib = await SeededAsync();

        // 15:09:26 in 5-minute buckets floors to 15:05:00.
        var bucket = await KeyAsync(lib, r => SqlFn.BucketUtc(r.At, 300));
        Assert.Equal(new DateTime(2026, 3, 14, 15, 5, 0, DateTimeKind.Utc), bucket.ToUniversalTime());

        // An hour-wide bucket floors to 15:00:00.
        var hourly = await KeyAsync(lib, r => SqlFn.BucketUtc(r.At, 3600));
        Assert.Equal(new DateTime(2026, 3, 14, 15, 0, 0, DateTimeKind.Utc), hourly.ToUniversalTime());
    }

    // ── Null handling ────────────────────────────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task IfNull_and_Coalesce_substitute_for_null()
    {
        var lib = await SeededAsync();

        // "other" is null in the seeded row.
        Assert.Equal("fallback", await KeyAsync(lib, r => SqlFn.IfNull(r.Other, "fallback")));
        Assert.Equal("MiXeD", await KeyAsync(lib, r => SqlFn.IfNull(r.Word, "unused")));

        // Coalesce takes the first non-null of many.
        Assert.Equal("MiXeD", await KeyAsync(lib, r => SqlFn.Coalesce(r.Other, r.Word, "last")));
    }

    // ── Strings ──────────────────────────────────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task Case_functions_fold_correctly()
    {
        var lib = await SeededAsync();
        Assert.Equal("mixed", await KeyAsync(lib, r => SqlFn.Lower(r.Word)));
        Assert.Equal("MIXED", await KeyAsync(lib, r => SqlFn.Upper(r.Word)));
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task Concat_joins_parts()
    {
        var lib = await SeededAsync();
        Assert.Equal("MiXeD!", await KeyAsync(lib, r => SqlFn.Concat(r.Word, "!")));
    }

    /// <summary>
    /// Unlike Contains/StartsWith, SqlFn.Like passes the pattern through verbatim, so the
    /// caller's wildcards are meant to be honoured.
    /// </summary>
    [FactRequiresEnv(Gate, Reason)]
    public async Task Like_honours_an_explicit_pattern()
    {
        var lib = await SeededAsync();
        Assert.True(await KeyAsync(lib, r => SqlFn.Like(r.Word, "Mi%")));
        Assert.False(await KeyAsync(lib, r => SqlFn.Like(r.Word, "zz%")));
    }

    // ── Numerics ─────────────────────────────────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task Rounding_functions_behave()
    {
        var lib = await SeededAsync();   // amount = 2.345

        Assert.Equal(2.0, await KeyAsync(lib, r => SqlFn.Floor(r.Amount)));
        Assert.Equal(3.0, await KeyAsync(lib, r => SqlFn.Ceiling(r.Amount)));
        Assert.Equal(2.35, await KeyAsync(lib, r => SqlFn.Round(r.Amount, 2)), 3);
        Assert.Equal(2.0, await KeyAsync(lib, r => SqlFn.Round(r.Amount, 0)), 3);
    }

    // ── Guard ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Calling_a_SqlFn_outside_a_query_throws_a_clear_error()
    {
        // These exist only to be translated; called directly they must say so rather than
        // return a silently wrong value.
        var ex = Assert.Throws<InvalidOperationException>(() => SqlFn.Lower("x"));
        Assert.Contains("Lower", ex.Message, StringComparison.Ordinal);
    }
}
