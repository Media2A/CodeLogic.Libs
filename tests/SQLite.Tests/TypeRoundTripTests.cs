using CL.SQLite;
using CL.SQLite.Models;
using Xunit;

namespace SQLite.Tests;

public enum Priority { Low = 0, Normal = 1, High = 7 }

[SQLiteTable("type_matrix")]
public sealed class TypeMatrix
{
    [SQLiteColumn(IsPrimaryKey = true, IsAutoIncrement = true, ColumnName = "id", DataType = SQLiteDataType.INTEGER)]
    public long Id { get; set; }

    [SQLiteColumn(ColumnName = "tag", DataType = SQLiteDataType.TEXT, IsNotNull = true)]
    public string Tag { get; set; } = "";

    [SQLiteColumn(ColumnName = "i32", DataType = SQLiteDataType.INTEGER)]
    public int I32 { get; set; }

    [SQLiteColumn(ColumnName = "i64", DataType = SQLiteDataType.INTEGER)]
    public long I64 { get; set; }

    [SQLiteColumn(ColumnName = "i16", DataType = SQLiteDataType.INTEGER)]
    public short I16 { get; set; }

    [SQLiteColumn(ColumnName = "dbl", DataType = SQLiteDataType.REAL)]
    public double Dbl { get; set; }

    [SQLiteColumn(ColumnName = "flt", DataType = SQLiteDataType.REAL)]
    public float Flt { get; set; }

    [SQLiteColumn(ColumnName = "dec", DataType = SQLiteDataType.NUMERIC)]
    public decimal Dec { get; set; }

    [SQLiteColumn(ColumnName = "txt", DataType = SQLiteDataType.TEXT)]
    public string? Txt { get; set; }

    [SQLiteColumn(ColumnName = "blob", DataType = SQLiteDataType.BLOB)]
    public byte[]? Blob { get; set; }

    [SQLiteColumn(ColumnName = "flag", DataType = SQLiteDataType.BOOLEAN)]
    public bool Flag { get; set; }

    [SQLiteColumn(ColumnName = "flag_n", DataType = SQLiteDataType.BOOLEAN)]
    public bool? FlagN { get; set; }

    [SQLiteColumn(ColumnName = "when_utc", DataType = SQLiteDataType.DATETIME)]
    public DateTime WhenUtc { get; set; }

    [SQLiteColumn(ColumnName = "when_n", DataType = SQLiteDataType.DATETIME)]
    public DateTime? WhenN { get; set; }

    [SQLiteColumn(ColumnName = "uid", DataType = SQLiteDataType.UUID)]
    public Guid Uid { get; set; }

    [SQLiteColumn(ColumnName = "uid_n", DataType = SQLiteDataType.UUID)]
    public Guid? UidN { get; set; }

    [SQLiteColumn(ColumnName = "prio", DataType = SQLiteDataType.INTEGER)]
    public Priority Prio { get; set; }

    [SQLiteColumn(ColumnName = "i32_n", DataType = SQLiteDataType.INTEGER)]
    public int? I32N { get; set; }
}

/// <summary><see cref="DateTimeOffset"/> is converted on the way in but not on the way out.</summary>
[SQLiteTable("type_dto")]
public sealed class DtoRow
{
    [SQLiteColumn(IsPrimaryKey = true, IsAutoIncrement = true, ColumnName = "id", DataType = SQLiteDataType.INTEGER)]
    public long Id { get; set; }

    [SQLiteColumn(ColumnName = "at", DataType = SQLiteDataType.DATETIME)]
    public DateTimeOffset At { get; set; }
}

[Collection("codelogic")]
public sealed class TypeRoundTripTests
{
    private readonly SQLiteRuntimeFixture _fx;
    private SQLiteLibrary Lib => _fx.Library;

    public TypeRoundTripTests(SQLiteRuntimeFixture fx) => _fx = fx;

    private async Task<Repositories> ReposAsync()
    {
        Assert.True((await Lib.TableSync.SyncTableAsync<TypeMatrix>()).IsSuccess);
        return new Repositories(Lib.GetRepository<TypeMatrix>(), Lib.GetQueryBuilder<TypeMatrix>());
    }

    private readonly record struct Repositories(
        CL.SQLite.Services.Repository<TypeMatrix> Repo,
        CL.SQLite.Services.QueryBuilder<TypeMatrix> Query);

    private static string Tag() => "ty" + Guid.NewGuid().ToString("N")[..10];

    // ── Full matrix ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Every_supported_scalar_type_survives_an_insert_and_a_read_back()
    {
        var (repo, _) = await ReposAsync();
        var when = new DateTime(2021, 12, 25, 13, 14, 15, 678, DateTimeKind.Utc);
        var uid = Guid.NewGuid();

        var original = new TypeMatrix
        {
            Tag = Tag(),
            I32 = -2_000_000_000,
            I64 = 9_007_199_254_740_993L,
            I16 = -12345,
            Dbl = 1.2345678901234e120,
            Flt = 2.5f,
            Dec = 123.45m,
            Txt = "hello",
            Blob = [0x00, 0x01, 0xFE, 0xFF],
            Flag = true,
            FlagN = false,
            WhenUtc = when,
            WhenN = when.AddDays(-1),
            Uid = uid,
            UidN = uid,
            Prio = Priority.High,
            I32N = 7
        };

        var id = await Insert(repo, original);
        var read = (await repo.GetByIdAsync(id)).Value!;

        Assert.Equal(original.Tag, read.Tag);
        Assert.Equal(original.I32, read.I32);
        Assert.Equal(original.I64, read.I64);
        Assert.Equal(original.I16, read.I16);
        Assert.Equal(original.Dbl, read.Dbl);
        Assert.Equal(original.Flt, read.Flt);
        Assert.Equal(original.Dec, read.Dec);
        Assert.Equal(original.Txt, read.Txt);
        Assert.Equal(original.Blob, read.Blob);
        Assert.True(read.Flag);
        Assert.False(read.FlagN);
        Assert.Equal(when, read.WhenUtc);
        Assert.Equal(when.AddDays(-1), read.WhenN);
        Assert.Equal(uid, read.Uid);
        Assert.Equal(uid, read.UidN);
        Assert.Equal(Priority.High, read.Prio);
        Assert.Equal(7, read.I32N);
    }

    [Fact]
    public async Task Every_nullable_column_round_trips_as_NULL()
    {
        var (repo, _) = await ReposAsync();

        var id = await Insert(repo, new TypeMatrix
        {
            Tag = Tag(),
            Txt = null, Blob = null, FlagN = null, WhenN = null, UidN = null, I32N = null
        });
        var read = (await repo.GetByIdAsync(id)).Value!;

        Assert.Null(read.Txt);
        Assert.Null(read.Blob);
        Assert.Null(read.FlagN);
        Assert.Null(read.WhenN);
        Assert.Null(read.UidN);
        Assert.Null(read.I32N);
    }

    [Fact]
    public async Task Default_values_of_the_non_nullable_columns_round_trip()
    {
        var (repo, _) = await ReposAsync();

        var id = await Insert(repo, new TypeMatrix { Tag = Tag() });
        var read = (await repo.GetByIdAsync(id)).Value!;

        Assert.Equal(0, read.I32);
        Assert.Equal(0d, read.Dbl);
        Assert.Equal(0m, read.Dec);
        Assert.False(read.Flag);
        Assert.Equal(Guid.Empty, read.Uid);
        Assert.Equal(Priority.Low, read.Prio);
        Assert.Equal(default, read.WhenUtc);
    }

    // ── Integers ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public async Task A_64_bit_integer_round_trips_at_the_extremes(long value)
    {
        var (repo, _) = await ReposAsync();

        var id = await Insert(repo, new TypeMatrix { Tag = Tag(), I64 = value });
        Assert.Equal(value, (await repo.GetByIdAsync(id)).Value!.I64);
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public async Task A_32_bit_integer_round_trips_at_the_extremes(int value)
    {
        var (repo, _) = await ReposAsync();

        var id = await Insert(repo, new TypeMatrix { Tag = Tag(), I32 = value });
        Assert.Equal(value, (await repo.GetByIdAsync(id)).Value!.I32);
    }

    // ── Reals ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0d)]
    [InlineData(-0.5d)]
    [InlineData(double.Epsilon)]
    [InlineData(double.MaxValue)]
    [InlineData(double.MinValue)]
    public async Task A_double_round_trips_exactly(double value)
    {
        var (repo, _) = await ReposAsync();

        var id = await Insert(repo, new TypeMatrix { Tag = Tag(), Dbl = value });
        Assert.Equal(value, (await repo.GetByIdAsync(id)).Value!.Dbl);
    }

    [Fact]
    public async Task A_decimal_keeps_its_scale_through_a_NUMERIC_column()
    {
        var (repo, _) = await ReposAsync();

        var id = await Insert(repo, new TypeMatrix { Tag = Tag(), Dec = 12345.6789m });
        Assert.Equal(12345.6789m, (await repo.GetByIdAsync(id)).Value!.Dec);
    }

    // ── Text ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("O'Brien")]
    [InlineData("double \" quote")]
    [InlineData("semi; colon -- comment /* block */")]
    [InlineData("unicode: æøå 日本語 🙂")]
    [InlineData("newline\r\nand\ttab")]
    [InlineData("null byte is not included")]
    public async Task Text_round_trips_verbatim_including_SQL_metacharacters(string value)
    {
        var (repo, _) = await ReposAsync();

        var id = await Insert(repo, new TypeMatrix { Tag = Tag(), Txt = value });
        Assert.Equal(value, (await repo.GetByIdAsync(id)).Value!.Txt);
    }

    [Fact]
    public async Task An_empty_string_stays_an_empty_string_and_is_distinct_from_NULL()
    {
        var (repo, query) = await ReposAsync();
        var tag = Tag();

        await Insert(repo, new TypeMatrix { Tag = tag, Txt = "" });
        await Insert(repo, new TypeMatrix { Tag = tag, Txt = null });

        var emptyRows = await Lib.GetQueryBuilder<TypeMatrix>()
            .Where(r => r.Tag == tag && r.Txt == "").ToListAsync();
        Assert.True(emptyRows.IsSuccess, emptyRows.Error?.Message);
        Assert.Single(emptyRows.Value!);

        var nullRows = await Lib.GetQueryBuilder<TypeMatrix>()
            .Where(r => r.Tag == tag && r.Txt == null).ToListAsync();
        Assert.True(nullRows.IsSuccess, nullRows.Error?.Message);
        Assert.Single(nullRows.Value!);
    }

    [Fact]
    public async Task A_long_string_round_trips()
    {
        var (repo, _) = await ReposAsync();
        var value = new string('x', 100_000);

        var id = await Insert(repo, new TypeMatrix { Tag = Tag(), Txt = value });
        Assert.Equal(value, (await repo.GetByIdAsync(id)).Value!.Txt);
    }

    // ── Blobs ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_blob_round_trips_byte_for_byte_including_embedded_zeros()
    {
        var (repo, _) = await ReposAsync();
        var bytes = new byte[256];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)i;

        var id = await Insert(repo, new TypeMatrix { Tag = Tag(), Blob = bytes });
        Assert.Equal(bytes, (await repo.GetByIdAsync(id)).Value!.Blob);
    }

    /// <summary>
    /// SQLite distinguishes a zero-length blob from NULL; Microsoft.Data.Sqlite maps an empty
    /// byte array onto a zero-length blob and reads it back the same way.
    /// </summary>
    [Fact]
    public async Task An_empty_blob_round_trips_as_an_empty_array_not_NULL()
    {
        var (repo, _) = await ReposAsync();

        var id = await Insert(repo, new TypeMatrix { Tag = Tag(), Blob = [] });
        var read = (await repo.GetByIdAsync(id)).Value!;
        Assert.NotNull(read.Blob);
        Assert.Empty(read.Blob!);
    }

    [Fact]
    public async Task A_large_blob_round_trips()
    {
        var (repo, _) = await ReposAsync();
        var bytes = new byte[512 * 1024];
        Random.Shared.NextBytes(bytes);

        var id = await Insert(repo, new TypeMatrix { Tag = Tag(), Blob = bytes });
        Assert.Equal(bytes, (await repo.GetByIdAsync(id)).Value!.Blob);
    }

    // ── Booleans ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_bool_is_stored_as_an_integer_one_or_zero()
    {
        var (repo, _) = await ReposAsync();
        var tag = Tag();
        await Insert(repo, new TypeMatrix { Tag = tag, Flag = true });

        var raw = await repo.RawQueryAsync(
            "SELECT \"flag\" FROM \"type_matrix\" WHERE \"tag\" = @t;",
            new Dictionary<string, object?> { ["@t"] = tag });
        Assert.True(raw.IsSuccess, raw.Error?.Message);
        Assert.True(Assert.Single(raw.Value!).Flag);

        var asInteger = await Lib.ConnectionManager.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT typeof(\"flag\"), \"flag\" FROM \"type_matrix\" WHERE \"tag\" = @t;";
            cmd.Parameters.AddWithValue("@t", tag);
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (reader.GetString(0), reader.GetInt64(1));
        });
        Assert.Equal("integer", asInteger.Item1);
        Assert.Equal(1L, asInteger.Item2);
    }

    [Fact]
    public async Task A_bool_column_can_be_filtered_on_in_a_WHERE_clause()
    {
        var (repo, _) = await ReposAsync();
        var tag = Tag();
        await Insert(repo, new TypeMatrix { Tag = tag, Flag = true, I32 = 1 });
        await Insert(repo, new TypeMatrix { Tag = tag, Flag = false, I32 = 2 });

        var onlyTrue = await Lib.GetQueryBuilder<TypeMatrix>()
            .Where(r => r.Tag == tag && r.Flag == true).ToListAsync();
        Assert.True(onlyTrue.IsSuccess, onlyTrue.Error?.Message);
        Assert.Equal(1, Assert.Single(onlyTrue.Value!).I32);
    }

    // ── DateTime ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_DateTime_round_trips_to_millisecond_precision()
    {
        var (repo, _) = await ReposAsync();
        var when = new DateTime(1999, 1, 2, 3, 4, 5, 678, DateTimeKind.Utc);

        var id = await Insert(repo, new TypeMatrix { Tag = Tag(), WhenUtc = when });
        Assert.Equal(when, (await repo.GetByIdAsync(id)).Value!.WhenUtc);
    }

    /// <summary>
    /// <c>ConvertToDbValue</c> formats a DateTime as <c>yyyy-MM-dd HH:mm:ss.fff</c>, so anything
    /// finer than a millisecond is discarded and the read-back value is the truncated one.
    /// </summary>
    [Fact]
    public async Task Sub_millisecond_precision_is_truncated_by_the_storage_format()
    {
        var (repo, _) = await ReposAsync();
        var when = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(1_234_567);

        var id = await Insert(repo, new TypeMatrix { Tag = Tag(), WhenUtc = when });
        var read = (await repo.GetByIdAsync(id)).Value!.WhenUtc;

        Assert.NotEqual(when, read);
        Assert.Equal(when.AddTicks(-(when.Ticks % TimeSpan.TicksPerMillisecond)), read);
    }

    /// <summary>
    /// The storage format carries no offset or kind marker, so a UTC DateTime comes back as
    /// <see cref="DateTimeKind.Unspecified"/>. Round-tripping a local time therefore silently
    /// loses the fact that it was local.
    /// </summary>
    [Fact]
    public async Task DateTimeKind_is_not_preserved()
    {
        var (repo, _) = await ReposAsync();
        var utc = new DateTime(2020, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        var id = await Insert(repo, new TypeMatrix { Tag = Tag(), WhenUtc = utc });
        var read = (await repo.GetByIdAsync(id)).Value!.WhenUtc;

        Assert.Equal(DateTimeKind.Unspecified, read.Kind);
        Assert.Equal(utc.Ticks, read.Ticks);
    }

    [Fact]
    public async Task DateTime_values_sort_and_compare_correctly_because_the_format_is_sortable()
    {
        var (repo, _) = await ReposAsync();
        var tag = Tag();
        var baseline = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await Insert(repo, new TypeMatrix { Tag = tag, WhenUtc = baseline.AddYears(1), I32 = 2 });
        await Insert(repo, new TypeMatrix { Tag = tag, WhenUtc = baseline, I32 = 1 });

        var ordered = await Lib.GetQueryBuilder<TypeMatrix>()
            .Where(r => r.Tag == tag).OrderBy(r => r.WhenUtc).ToListAsync();
        Assert.True(ordered.IsSuccess, ordered.Error?.Message);
        Assert.Equal([1, 2], ordered.Value!.Select(r => r.I32));

        var cutoff = baseline.AddMonths(6);
        var after = await Lib.GetQueryBuilder<TypeMatrix>()
            .Where(r => r.Tag == tag && r.WhenUtc > cutoff).ToListAsync();
        Assert.True(after.IsSuccess, after.Error?.Message);
        Assert.Equal(2, Assert.Single(after.Value!).I32);
    }

    /// <summary>
    /// <c>SQLiteValueConverter</c> writes a <see cref="DateTimeOffset"/> as
    /// <c>yyyy-MM-dd HH:mm:ss.fffzzz</c> and parses the same shape back, offset included.
    /// </summary>
    [Fact]
    public async Task A_DateTimeOffset_round_trips()
    {
        Assert.True((await Lib.TableSync.SyncTableAsync<DtoRow>()).IsSuccess);
        var repo = Lib.GetRepository<DtoRow>();
        var at = new DateTimeOffset(2020, 5, 6, 7, 8, 9, TimeSpan.FromHours(2));

        var id = await repo.InsertAsync(new DtoRow { At = at });
        Assert.True(id.IsSuccess, id.Error?.ToString());

        var read = await repo.GetByIdAsync(id.Value);
        Assert.True(read.IsSuccess, read.Error?.ToString());
        Assert.Equal(at, read.Value!.At);
    }

    /// <summary>The offset itself survives, not just the instant it denotes.</summary>
    [Fact]
    public async Task A_DateTimeOffset_keeps_its_offset_rather_than_being_normalized_to_UTC()
    {
        Assert.True((await Lib.TableSync.SyncTableAsync<DtoRow>()).IsSuccess);
        var repo = Lib.GetRepository<DtoRow>();

        var at = new DateTimeOffset(2020, 5, 6, 7, 8, 9, TimeSpan.FromHours(2));
        var id = await repo.InsertAsync(new DtoRow { At = at });
        Assert.True(id.IsSuccess, id.Error?.ToString());

        var read = await repo.GetByIdAsync(id.Value);
        Assert.True(read.IsSuccess, read.Error?.ToString());
        Assert.Equal(TimeSpan.FromHours(2), read.Value!.At.Offset);
        Assert.Equal(7, read.Value.At.Hour);
    }

    // ── Guid ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_Guid_is_stored_as_its_dashed_text_form_and_reads_back_equal()
    {
        var (repo, _) = await ReposAsync();
        var uid = Guid.NewGuid();
        var tag = Tag();

        var id = await Insert(repo, new TypeMatrix { Tag = tag, Uid = uid });
        Assert.Equal(uid, (await repo.GetByIdAsync(id)).Value!.Uid);

        var stored = await Lib.ConnectionManager.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT \"uid\" FROM \"type_matrix\" WHERE \"tag\" = @t;";
            cmd.Parameters.AddWithValue("@t", tag);
            return (string)(await cmd.ExecuteScalarAsync())!;
        });
        Assert.Equal(uid.ToString(), stored);
    }

    [Fact]
    public async Task An_empty_Guid_round_trips_and_is_distinct_from_NULL()
    {
        var (repo, _) = await ReposAsync();

        var id = await Insert(repo, new TypeMatrix { Tag = Tag(), Uid = Guid.Empty, UidN = Guid.Empty });
        var read = (await repo.GetByIdAsync(id)).Value!;
        Assert.Equal(Guid.Empty, read.Uid);
        Assert.Equal(Guid.Empty, read.UidN);
    }

    // ── Enums ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Priority.Low)]
    [InlineData(Priority.Normal)]
    [InlineData(Priority.High)]
    public async Task An_enum_round_trips_through_its_numeric_value(Priority prio)
    {
        var (repo, _) = await ReposAsync();

        var id = await Insert(repo, new TypeMatrix { Tag = Tag(), Prio = prio });
        Assert.Equal(prio, (await repo.GetByIdAsync(id)).Value!.Prio);
    }

    [Fact]
    public async Task An_enum_value_outside_the_declared_members_still_round_trips()
    {
        var (repo, _) = await ReposAsync();

        var id = await Insert(repo, new TypeMatrix { Tag = Tag(), Prio = (Priority)99 });
        Assert.Equal((Priority)99, (await repo.GetByIdAsync(id)).Value!.Prio);
    }

    [Fact]
    public async Task An_enum_column_can_be_compared_in_a_WHERE_clause()
    {
        var (repo, _) = await ReposAsync();
        var tag = Tag();
        await Insert(repo, new TypeMatrix { Tag = tag, Prio = Priority.High, I32 = 1 });
        await Insert(repo, new TypeMatrix { Tag = tag, Prio = Priority.Low, I32 = 2 });

        var high = await Lib.GetQueryBuilder<TypeMatrix>()
            .Where(r => r.Tag == tag && r.Prio == Priority.High).ToListAsync();
        Assert.True(high.IsSuccess, high.Error?.Message);
        Assert.Equal(1, Assert.Single(high.Value!).I32);
    }

    // ── Update round trip ─────────────────────────────────────────────────────

    [Fact]
    public async Task Updating_a_row_rewrites_every_converted_type_correctly()
    {
        var (repo, _) = await ReposAsync();
        var id = await Insert(repo, new TypeMatrix { Tag = Tag(), Flag = false, Prio = Priority.Low });

        var uid = Guid.NewGuid();
        var when = new DateTime(2022, 2, 2, 2, 2, 2, 222, DateTimeKind.Utc);
        var entity = (await repo.GetByIdAsync(id)).Value!;
        entity.Flag = true;
        entity.Prio = Priority.High;
        entity.Uid = uid;
        entity.WhenUtc = when;
        entity.Blob = [1, 2, 3];
        Assert.True((await repo.UpdateAsync(entity)).IsSuccess);

        var read = (await repo.GetByIdAsync(id)).Value!;
        Assert.True(read.Flag);
        Assert.Equal(Priority.High, read.Prio);
        Assert.Equal(uid, read.Uid);
        Assert.Equal(when, read.WhenUtc);
        Assert.Equal<byte[]>([1, 2, 3], read.Blob!);
    }

    private static async Task<long> Insert(CL.SQLite.Services.Repository<TypeMatrix> repo, TypeMatrix row)
    {
        var result = await repo.InsertAsync(row);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        return result.Value;
    }
}
