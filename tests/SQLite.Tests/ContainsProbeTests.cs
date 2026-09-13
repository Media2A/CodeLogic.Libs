using CL.SQLite;
using Xunit;
using Xunit.Abstractions;

namespace SQLite.Tests;

/// <summary>
/// Probes collection membership translation. The sibling libraries had a bug where
/// <c>ids.Contains(x.Id)</c> on a <see cref="List{T}"/> was translated as a string
/// <c>LIKE</c>; CL.SQLite guards on <c>DeclaringType == typeof(string)</c> so it cannot
/// hit that, but its collection branch reads the collection from
/// <c>Arguments[0]</c> and the column from <c>Object</c>, which are the wrong way round
/// for the one-argument instance form. These tests establish which shapes actually work.
/// </summary>
[Collection("codelogic")]
public sealed class ContainsProbeTests
{
    private readonly SQLiteRuntimeFixture _fx;
    private SQLiteLibrary Lib => _fx.Library;
    private readonly ITestOutputHelper _out;

    public ContainsProbeTests(SQLiteRuntimeFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _out = output;
    }

    private async Task<(string Tag, long First)> SeedAsync()
    {
        await Lib.TableSync.SyncTableAsync<Widget>();
        var repo = Lib.GetRepository<Widget>();
        var tag = "in-" + Guid.NewGuid().ToString("N")[..8];
        var a = (await repo.InsertAsync(new Widget { Name = tag, Quantity = 1 })).Value;
        await repo.InsertAsync(new Widget { Name = tag, Quantity = 2 });
        return (tag, a);
    }

    [Fact]
    public async Task Array_Contains_via_the_static_two_argument_form()
    {
        var (tag, first) = await SeedAsync();
        var ids = new[] { first };

        var result = await Lib.GetQueryBuilder<Widget>()
            .Where(w => w.Name == tag && ids.Contains(w.Id))
            .ToListAsync();

        _out.WriteLine(result.IsSuccess ? "ok" : result.Error?.Message ?? "failed");
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Single(result.Value!);
    }

    [Fact]
    public async Task List_Contains_via_the_one_argument_instance_form()
    {
        var (tag, first) = await SeedAsync();
        var ids = new List<long> { first };

        var result = await Lib.GetQueryBuilder<Widget>()
            .Where(w => w.Name == tag && ids.Contains(w.Id))
            .ToListAsync();

        _out.WriteLine(result.IsSuccess ? "ok" : result.Error?.Message ?? "failed");
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Single(result.Value!);
    }

    [Fact]
    public async Task String_Contains_is_a_LIKE()
    {
        var (tag, _) = await SeedAsync();

        var needle = tag[3..];
        var result = await Lib.GetQueryBuilder<Widget>()
            .Where(w => w.Name.Contains(needle))
            .ToListAsync();

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(2, result.Value!.Count);
    }
}
