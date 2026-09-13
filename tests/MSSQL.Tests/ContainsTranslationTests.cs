using CL.MSSQL;
using CL.MSSQL.Models;
using Xunit;

namespace MSSQL.Tests;

[Table(Name = "it_contains", Schema = "dbo")]
public sealed class ContainsRow
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(Name = "name", DataType = DataType.NVarChar, Size = 50, NotNull = true)]
    public string Name { get; set; } = "";

    [Column(Name = "vip", DataType = DataType.Bit, NotNull = true)]
    public bool Vip { get; set; }
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
    private readonly MSSQLRuntimeFixture _fx;
    private MSSQLLibrary Mssql => _fx.Mysql;

    public ContainsTranslationTests(MSSQLRuntimeFixture fx) => _fx = fx;

    private async Task<(long Alice, long Bob)> SeedAsync()
    {
        await _fx.Exec("DROP TABLE IF EXISTS [dbo].[it_contains]");
        Assert.True((await Mssql.SyncTableAsync<ContainsRow>(createBackup: false)).IsSuccess);

        var repo = Mssql.GetRepository<ContainsRow>();
        var alice = (await repo.InsertAsync(new ContainsRow { Name = "Alice", Vip = true })).Value!;
        var bob = (await repo.InsertAsync(new ContainsRow { Name = "Bob", Vip = false })).Value!;
        return (alice.Id, bob.Id);
    }

    [DbFact]
    public async Task List_Contains_on_a_column_becomes_an_IN_clause()
    {
        var (alice, bob) = await SeedAsync();
        var ids = new List<long> { alice, bob };

        var result = await Mssql.Query<ContainsRow>().Where(r => ids.Contains(r.Id)).ToListAsync();

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(2, result.Value!.Count);
    }

    [DbFact]
    public async Task HashSet_and_array_agree_with_List()
    {
        var (alice, _) = await SeedAsync();
        var set = new HashSet<long> { alice };
        var array = new[] { alice };
        var list = new List<long> { alice };

        var fromSet = await Mssql.Query<ContainsRow>().Where(r => set.Contains(r.Id)).ToListAsync();
        var fromArray = await Mssql.Query<ContainsRow>().Where(r => array.Contains(r.Id)).ToListAsync();
        var fromList = await Mssql.Query<ContainsRow>().Where(r => list.Contains(r.Id)).ToListAsync();

        Assert.True(fromSet.IsSuccess, fromSet.Error?.ToString());
        Assert.True(fromArray.IsSuccess, fromArray.Error?.ToString());
        Assert.True(fromList.IsSuccess, fromList.Error?.ToString());
        Assert.Single(fromSet.Value!);
        Assert.Single(fromArray.Value!);
        Assert.Single(fromList.Value!);
    }

    [DbFact]
    public async Task An_empty_list_matches_nothing_rather_than_emitting_IN_parens()
    {
        await SeedAsync();
        var none = new List<long>();

        var result = await Mssql.Query<ContainsRow>().Where(r => none.Contains(r.Id)).ToListAsync();

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Empty(result.Value!);
    }

    /// <summary>
    /// The narrowing guard must not cost us the string overload: a string receiver is still
    /// a substring test, not a membership test over its characters.
    /// </summary>
    [DbFact]
    public async Task String_Contains_is_still_a_LIKE()
    {
        await SeedAsync();

        var result = await Mssql.Query<ContainsRow>().Where(r => r.Name.Contains("lic")).ToListAsync();

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Single(result.Value!);
        Assert.Equal("Alice", result.Value![0].Name);
    }

    [DbFact]
    public async Task StartsWith_and_EndsWith_are_unaffected()
    {
        await SeedAsync();

        var starts = await Mssql.Query<ContainsRow>().Where(r => r.Name.StartsWith("Al")).ToListAsync();
        var ends = await Mssql.Query<ContainsRow>().Where(r => r.Name.EndsWith("ob")).ToListAsync();

        Assert.True(starts.IsSuccess, starts.Error?.ToString());
        Assert.True(ends.IsSuccess, ends.Error?.ToString());
        Assert.Single(starts.Value!);
        Assert.Single(ends.Value!);
    }

    [DbFact]
    public async Task List_Contains_composes_with_other_predicates()
    {
        var (alice, bob) = await SeedAsync();
        var ids = new List<long> { alice, bob };

        var result = await Mssql.Query<ContainsRow>()
            .Where(r => ids.Contains(r.Id) && r.Vip)
            .ToListAsync();

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Single(result.Value!);
        Assert.Equal("Alice", result.Value![0].Name);
    }
}
