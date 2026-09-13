using CL.SQLite;
using CL.SQLite.Models;
using Xunit;
using Xunit.Abstractions;

namespace SQLite.Tests;

[SQLiteTable("probe_guid_key")]
public sealed class GuidKeyed
{
    [SQLiteColumn(IsPrimaryKey = true, ColumnName = "id", DataType = SQLiteDataType.TEXT)]
    public Guid Id { get; set; }

    [SQLiteColumn(ColumnName = "label", DataType = SQLiteDataType.TEXT)]
    public string Label { get; set; } = "";
}

[SQLiteTable("probe_converted")]
public sealed class ConvertedValues
{
    [SQLiteColumn(IsPrimaryKey = true, IsAutoIncrement = true, ColumnName = "id", DataType = SQLiteDataType.INTEGER)]
    public long Id { get; set; }

    [SQLiteColumn(ColumnName = "when_utc", DataType = SQLiteDataType.TEXT)]
    public DateTime WhenUtc { get; set; }

    [SQLiteColumn(ColumnName = "ref_id", DataType = SQLiteDataType.TEXT)]
    public Guid RefId { get; set; }

    [SQLiteColumn(ColumnName = "active", DataType = SQLiteDataType.INTEGER)]
    public bool Active { get; set; }
}

/// <summary>
/// The repository converts values on the way in through the shared value converter, but two
/// other paths bind whatever the caller handed over: the bulk
/// <see cref="QueryBuilder{T}.UpdateAsync"/> dictionary, and primary-key values on the
/// by-key read/delete paths. Microsoft.Data.Sqlite maps a raw <see cref="Guid"/> to a BLOB
/// and a raw <see cref="DateTime"/> to its own text format, so the two paths disagree about
/// what a value looks like on disk. The three sibling libraries route both through their
/// type converter and an allow-list; these tests pin the same contract here.
/// </summary>
[Collection("codelogic")]
public sealed class ValueBindingProbeTests
{
    private readonly SQLiteRuntimeFixture _fx;
    private SQLiteLibrary Lib => _fx.Library;
    private readonly ITestOutputHelper _out;

    public ValueBindingProbeTests(SQLiteRuntimeFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _out = output;
    }

    [Fact]
    public async Task A_Guid_keyed_row_can_be_read_back_by_its_key()
    {
        await Lib.TableSync.SyncTableAsync<GuidKeyed>();
        var repo = Lib.GetRepository<GuidKeyed>();
        var id = Guid.NewGuid();
        Assert.True((await repo.InsertAsync(new GuidKeyed { Id = id, Label = "x" })).IsSuccess);

        var found = await repo.GetByKeysAsync(default, id);

        _out.WriteLine(found.IsSuccess ? $"value={found.Value?.Label ?? "<null>"}" : found.Error!.Message);
        Assert.True(found.IsSuccess, found.Error?.Message);
        Assert.NotNull(found.Value);
        Assert.Equal("x", found.Value!.Label);
    }

    [Fact]
    public async Task A_Guid_keyed_row_can_be_deleted_by_its_key()
    {
        await Lib.TableSync.SyncTableAsync<GuidKeyed>();
        var repo = Lib.GetRepository<GuidKeyed>();
        var id = Guid.NewGuid();
        await repo.InsertAsync(new GuidKeyed { Id = id, Label = "doomed" });

        var deleted = await repo.DeleteByKeysAsync(default, id);

        Assert.True(deleted.IsSuccess, deleted.Error?.Message);
        // Counting is the only honest check here: a lookup by the same key would fail to
        // match for exactly the reason the delete failed to match, and pass vacuously.
        var remaining = await repo.RawQueryAsync(
            "SELECT * FROM \"probe_guid_key\" WHERE \"label\" = 'doomed'");
        Assert.True(remaining.IsSuccess, remaining.Error?.Message);
        Assert.Empty(remaining.Value!);
    }

    [Fact]
    public async Task Bulk_update_writes_the_same_representation_the_repository_writes()
    {
        await Lib.TableSync.SyncTableAsync<ConvertedValues>();
        var repo = Lib.GetRepository<ConvertedValues>();

        var when = new DateTime(2026, 3, 14, 15, 9, 26, DateTimeKind.Utc);
        var reference = Guid.NewGuid();
        var id = (await repo.InsertAsync(new ConvertedValues
        {
            WhenUtc = when, RefId = reference, Active = true,
        })).Value;

        // Same logical values, written through the bulk path instead.
        var updated = await Lib.GetQueryBuilder<ConvertedValues>()
            .Where(r => r.Id == id)
            .UpdateAsync(new Dictionary<string, object?>
            {
                ["when_utc"] = when,
                ["ref_id"] = reference,
                ["active"] = true,
            });
        Assert.True(updated.IsSuccess, updated.Error?.Message);
        Assert.Equal(1, updated.Value);

        // Round-tripping must still work: if the bulk path stored a different
        // representation, materialization reads back something else or throws.
        var read = await repo.GetByIdAsync(id);
        Assert.True(read.IsSuccess, read.Error?.Message);
        Assert.NotNull(read.Value);
        Assert.Equal(when, read.Value!.WhenUtc);
        Assert.Equal(reference, read.Value.RefId);
        Assert.True(read.Value.Active);
    }

    /// <summary>
    /// An unmapped key is already rejected by SQLite itself ("no such column"), so that on
    /// its own proves nothing. What the sibling libraries' allow-list actually buys is that
    /// a key cannot escape its quoted identifier and add clauses of its own.
    /// </summary>
    [Fact]
    public async Task A_dictionary_key_cannot_escape_its_quoted_identifier()
    {
        await Lib.TableSync.SyncTableAsync<ConvertedValues>();
        var repo = Lib.GetRepository<ConvertedValues>();
        var keep = (await repo.InsertAsync(new ConvertedValues
        {
            WhenUtc = DateTime.UtcNow, RefId = Guid.NewGuid(), Active = true,
        })).Value;

        // Closes the quoted identifier and appends a second assignment. If the key reaches
        // the SET clause verbatim this updates a column the caller never named.
        var hostile = new Dictionary<string, object?>
        {
            ["when_utc\" = '1999-01-01 00:00:00', \"active"] = DateTime.UtcNow,
        };

        var result = await Lib.GetQueryBuilder<ConvertedValues>().UpdateAsync(hostile);
        _out.WriteLine(result.IsSuccess ? "ACCEPTED - injected" : $"rejected: {result.Error!.Message}");

        var row = (await repo.GetByIdAsync(keep)).Value;
        Assert.NotNull(row);
        Assert.True(row!.Active, "a column the caller never named was overwritten");
    }
}
