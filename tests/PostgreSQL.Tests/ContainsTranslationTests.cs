using CL.PostgreSQL;
using CL.PostgreSQL.Models;
using Xunit;

namespace PostgreSQL.Tests;

[Table(Name = "it_contains")]
public sealed class ContainsRow
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "name", Size = 50, NotNull = true)] public string Name { get; set; } = "";
    [Column(Name = "vip", NotNull = true)] public bool Vip { get; set; }
}

/// <summary>
/// <c>ids.Contains(x.Id)</c> on a <see cref="List{T}"/> used to be translated as a string
/// <c>LIKE</c>: the visitor's first <c>Contains</c> case matched any single-argument
/// instance call, so the receiver — the list — was emitted as if it were a column, and the
/// query threw. Arrays escaped it because they bind to the static two-argument
/// <c>Enumerable.Contains</c>, which had its own case. Since a <c>List&lt;T&gt;</c> is the
/// idiomatic way to write an IN clause in C#, this broke ordinary usage.
/// </summary>
[Collection("codelogic")]
public sealed class ContainsTranslationTests
{
    private const string Gate = "CL_PG_TEST_HOST";
    private const string Reason = "set CL_PG_TEST_HOST (+ _PORT/_DB/_USER/_PASS) to run live PostgreSQL tests";

    private readonly PostgreSQLRuntimeFixture _fx;
    private PostgreSQLLibrary Lib => _fx.Library ?? throw new InvalidOperationException("Runtime not booted.");

    public ContainsTranslationTests(PostgreSQLRuntimeFixture fx) => _fx = fx;

    private async Task<(long Alice, long Bob)> SeedAsync()
    {
        await Lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_contains\" CASCADE");
        Assert.True((await Lib.SyncTableAsync<ContainsRow>(createBackup: false)).IsSuccess);

        var repo = Lib.GetRepository<ContainsRow>();
        var alice = (await repo.InsertAsync(new ContainsRow { Name = "Alice", Vip = true })).Value!;
        var bob = (await repo.InsertAsync(new ContainsRow { Name = "Bob", Vip = false })).Value!;
        return (alice.Id, bob.Id);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task List_Contains_on_a_column_becomes_an_IN_clause()
    {
        var (alice, bob) = await SeedAsync();
        var ids = new List<long> { alice, bob };

        var result = await Lib.Query<ContainsRow>().Where(r => ids.Contains(r.Id)).ToListAsync();

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(2, result.Value!.Count);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task HashSet_and_array_agree_with_List()
    {
        var (alice, _) = await SeedAsync();
        var set = new HashSet<long> { alice };
        var array = new[] { alice };
        var list = new List<long> { alice };

        var fromSet = await Lib.Query<ContainsRow>().Where(r => set.Contains(r.Id)).ToListAsync();
        var fromArray = await Lib.Query<ContainsRow>().Where(r => array.Contains(r.Id)).ToListAsync();
        var fromList = await Lib.Query<ContainsRow>().Where(r => list.Contains(r.Id)).ToListAsync();

        Assert.True(fromSet.IsSuccess, fromSet.Error?.ToString());
        Assert.True(fromArray.IsSuccess, fromArray.Error?.ToString());
        Assert.True(fromList.IsSuccess, fromList.Error?.ToString());
        Assert.Single(fromSet.Value!);
        Assert.Single(fromArray.Value!);
        Assert.Single(fromList.Value!);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task An_empty_list_matches_nothing_rather_than_emitting_IN_parens()
    {
        await SeedAsync();
        var none = new List<long>();

        var result = await Lib.Query<ContainsRow>().Where(r => none.Contains(r.Id)).ToListAsync();

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Empty(result.Value!);
    }

    /// <summary>
    /// The narrowing guard must not cost us the string overload: a string receiver is still
    /// a substring test, not a membership test over its characters.
    /// </summary>
    [FactRequiresEnv(Gate, Reason)]
    public async Task String_Contains_is_still_a_LIKE()
    {
        await SeedAsync();

        var result = await Lib.Query<ContainsRow>().Where(r => r.Name.Contains("lic")).ToListAsync();

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Single(result.Value!);
        Assert.Equal("Alice", result.Value![0].Name);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task StartsWith_and_EndsWith_are_unaffected()
    {
        await SeedAsync();

        var starts = await Lib.Query<ContainsRow>().Where(r => r.Name.StartsWith("Al")).ToListAsync();
        var ends = await Lib.Query<ContainsRow>().Where(r => r.Name.EndsWith("ob")).ToListAsync();

        Assert.True(starts.IsSuccess, starts.Error?.ToString());
        Assert.True(ends.IsSuccess, ends.Error?.ToString());
        Assert.Single(starts.Value!);
        Assert.Single(ends.Value!);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task List_Contains_composes_with_other_predicates()
    {
        var (alice, bob) = await SeedAsync();
        var ids = new List<long> { alice, bob };

        var result = await Lib.Query<ContainsRow>()
            .Where(r => ids.Contains(r.Id) && r.Vip)
            .ToListAsync();

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Single(result.Value!);
        Assert.Equal("Alice", result.Value![0].Name);
    }
}
