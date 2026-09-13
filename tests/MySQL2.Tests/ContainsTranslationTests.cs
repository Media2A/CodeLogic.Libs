using CL.MySQL2;
using Xunit;

namespace MySQL2.Tests;

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
    private readonly MySQL2RuntimeFixture _fx;
    private MySQL2Library Mysql => _fx.Mysql;

    public ContainsTranslationTests(MySQL2RuntimeFixture fx) => _fx = fx;

    [DbFact]
    public async Task List_Contains_on_a_column_becomes_an_IN_clause()
    {
        var ids = new List<long> { _fx.AliceId, _fx.BobId };

        var result = await Mysql.Query<Customer>().Where(c => ids.Contains(c.Id)).ToListAsync();

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(2, result.Value!.Count);
        Assert.Contains(result.Value, c => c.Name == "Alice");
        Assert.Contains(result.Value, c => c.Name == "Bob");
    }

    [DbFact]
    public async Task HashSet_and_array_agree_with_List()
    {
        var set = new HashSet<long> { _fx.AliceId };
        var array = new[] { _fx.AliceId };
        var list = new List<long> { _fx.AliceId };

        var fromSet = await Mysql.Query<Customer>().Where(c => set.Contains(c.Id)).ToListAsync();
        var fromArray = await Mysql.Query<Customer>().Where(c => array.Contains(c.Id)).ToListAsync();
        var fromList = await Mysql.Query<Customer>().Where(c => list.Contains(c.Id)).ToListAsync();

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
        var none = new List<long>();
        var result = await Mysql.Query<Customer>().Where(c => none.Contains(c.Id)).ToListAsync();

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
        var result = await Mysql.Query<Customer>().Where(c => c.Name.Contains("lic")).ToListAsync();

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Single(result.Value!);
        Assert.Equal("Alice", result.Value![0].Name);
    }

    [DbFact]
    public async Task StartsWith_and_EndsWith_are_unaffected()
    {
        var starts = await Mysql.Query<Customer>().Where(c => c.Name.StartsWith("Al")).ToListAsync();
        var ends = await Mysql.Query<Customer>().Where(c => c.Name.EndsWith("ob")).ToListAsync();

        Assert.True(starts.IsSuccess, starts.Error?.ToString());
        Assert.True(ends.IsSuccess, ends.Error?.ToString());
        Assert.Single(starts.Value!);
        Assert.Equal("Alice", starts.Value![0].Name);
        Assert.Single(ends.Value!);
        Assert.Equal("Bob", ends.Value![0].Name);
    }

    [DbFact]
    public async Task List_Contains_composes_with_other_predicates()
    {
        var ids = new List<long> { _fx.AliceId, _fx.BobId };

        var result = await Mysql.Query<Customer>()
            .Where(c => ids.Contains(c.Id) && c.IsVip)
            .ToListAsync();

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Single(result.Value!);
        Assert.Equal("Alice", result.Value![0].Name);
    }
}
