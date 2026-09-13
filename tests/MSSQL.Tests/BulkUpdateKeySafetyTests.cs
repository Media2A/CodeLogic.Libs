using CL.MSSQL;
using Xunit;
using Xunit.Abstractions;

namespace MSSQL.Tests;

/// <summary>
/// The bulk <c>UpdateAsync</c> dictionary takes caller-supplied keys. They are resolved
/// through <c>EntityMetadata&lt;T&gt;.RequireColumn</c> and only the resolved column name is
/// ever quoted into the SET clause, so a key cannot escape its own identifier and assign to
/// a column the caller never named. CL.SQLite interpolated the raw key and was vulnerable;
/// this pins the property here so the three cannot drift apart.
/// </summary>
[Collection("codelogic")]
public sealed class BulkUpdateKeySafetyTests
{
    private readonly MSSQLRuntimeFixture _fx;
    private MSSQLLibrary Db => _fx.Mysql;
    private readonly ITestOutputHelper _out;

    public BulkUpdateKeySafetyTests(MSSQLRuntimeFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _out = output;
    }

    [DbFact]
    public async Task A_hostile_dictionary_key_is_rejected()
    {
        var before = (await Db.Query<Customer>().CountAsync()).Value;

        var hostile = new Dictionary<string, object?> { ["name] = 'hacked', [is_vip"] = "x" };

        var result = await Db.Query<Customer>().UpdateAsync(hostile);

        _out.WriteLine(result.IsSuccess ? "ACCEPTED - injected" : $"rejected: {result.Error!.Message}");
        Assert.False(result.IsSuccess, "a key that is not a mapped column must be rejected");
        Assert.Equal(before, (await Db.Query<Customer>().CountAsync()).Value);
        Assert.True((await Db.Query<Customer>().Where(c => c.IsVip).CountAsync()).Value > 0,
            "a column the caller never named was overwritten");
    }

    [DbFact]
    public async Task An_unmapped_but_harmless_key_is_also_rejected()
    {
        var result = await Db.Query<Customer>()
            .UpdateAsync(new Dictionary<string, object?> { ["not_a_column"] = 1 });

        Assert.False(result.IsSuccess, "an unmapped key must not reach the SET clause");
    }
}
