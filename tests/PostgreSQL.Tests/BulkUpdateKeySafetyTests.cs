using CL.PostgreSQL;
using Xunit;
using Xunit.Abstractions;

namespace PostgreSQL.Tests;

/// <summary>
/// The bulk <c>UpdateAsync</c> dictionary takes caller-supplied keys. They are resolved
/// through <c>EntityMetadata&lt;T&gt;.RequireColumn</c> and only the resolved column name is
/// ever quoted into the SET clause, so a key cannot escape its own identifier and assign to
/// a column the caller never named. CL.SQLite interpolated the raw key and was vulnerable;
/// this pins the property here so the four cannot drift apart.
/// </summary>
[Collection("codelogic")]
public sealed class BulkUpdateKeySafetyTests
{
    private const string Gate = "CL_PG_TEST_HOST";
    private const string Reason = "set CL_PG_TEST_HOST (+ _PORT/_DB/_USER/_PASS) to run live PostgreSQL tests";

    private readonly PostgreSQLRuntimeFixture _fx;
    private PostgreSQLLibrary Lib => _fx.Library ?? throw new InvalidOperationException("Runtime not booted.");
    private readonly ITestOutputHelper _out;

    public BulkUpdateKeySafetyTests(PostgreSQLRuntimeFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _out = output;
    }

    private async Task SeedAsync()
    {
        await Lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_contains\" CASCADE");
        Assert.True((await Lib.SyncTableAsync<ContainsRow>(createBackup: false)).IsSuccess);
        await Lib.GetRepository<ContainsRow>().InsertAsync(new ContainsRow { Name = "keep", Vip = true });
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task A_hostile_dictionary_key_is_rejected()
    {
        await SeedAsync();

        // Closes the quoted identifier and appends an assignment to a different column.
        var hostile = new Dictionary<string, object?>
        {
            ["name\" = 'hacked', \"vip"] = "x",
        };

        var result = await Lib.Query<ContainsRow>().UpdateAsync(hostile);

        _out.WriteLine(result.IsSuccess ? "ACCEPTED - injected" : $"rejected: {result.Error!.Message}");
        Assert.False(result.IsSuccess, "a key that is not a mapped column must be rejected");

        var row = (await Lib.GetRepository<ContainsRow>().GetAllAsync()).Value!.Single();
        Assert.Equal("keep", row.Name);
        Assert.True(row.Vip, "a column the caller never named was overwritten");
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task An_unmapped_but_harmless_key_is_also_rejected()
    {
        await SeedAsync();

        var result = await Lib.Query<ContainsRow>()
            .UpdateAsync(new Dictionary<string, object?> { ["not_a_column"] = 1 });

        Assert.False(result.IsSuccess, "an unmapped key must not reach the SET clause");
    }
}
