using System.Net;
using CL.PostgreSQL;
using CL.PostgreSQL.Models;
using Xunit;

namespace PostgreSQL.Tests;

// Values, not just schema. A correct DDL type proves nothing about whether the value
// converter and the compiled materializer can put a CLR value in and get it back out.

[Table(Name = "it_rt_all", Schema = "public")]
public sealed class RoundTripRow
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }

    [Column(Name = "c_short")]    public short CShort { get; set; }
    [Column(Name = "c_int")]      public int CInt { get; set; }
    [Column(Name = "c_long")]     public long CLong { get; set; }
    [Column(Name = "c_float")]    public float CFloat { get; set; }
    [Column(Name = "c_double")]   public double CDouble { get; set; }
    [Column(Name = "c_decimal", DataType = DataType.Numeric, Precision = 18, Scale = 4)]
    public decimal CDecimal { get; set; }

    [Column(Name = "c_string", Size = 120)] public string CString { get; set; } = "";
    [Column(Name = "c_text", DataType = DataType.Text)] public string CText { get; set; } = "";
    [Column(Name = "c_bool")]     public bool CBool { get; set; }
    [Column(Name = "c_guid")]     public Guid CGuid { get; set; }

    [Column(Name = "c_dt")]       public DateTime CDateTime { get; set; }
    [Column(Name = "c_dto")]      public DateTimeOffset CDateTimeOffset { get; set; }
    [Column(Name = "c_date")]     public DateOnly CDate { get; set; }
    [Column(Name = "c_time")]     public TimeOnly CTime { get; set; }
    [Column(Name = "c_span")]     public TimeSpan CSpan { get; set; }

    [Column(Name = "c_bytes")]    public byte[] CBytes { get; set; } = [];
    [Column(Name = "c_jsonb", DataType = DataType.Jsonb)] public string CJsonb { get; set; } = "{}";
    [Column(Name = "c_enum")]     public DayOfWeek CEnum { get; set; }

    [Column(Name = "c_ints")]     public int[] CInts { get; set; } = [];
    [Column(Name = "c_strings")]  public string[] CStrings { get; set; } = [];

    // Nullables must round-trip as null, not as a default.
    [Column(Name = "n_int")]      public int? NInt { get; set; }
    [Column(Name = "n_string", Size = 50)] public string? NString { get; set; }
    [Column(Name = "n_guid")]     public Guid? NGuid { get; set; }
    [Column(Name = "n_dt")]       public DateTime? NDateTime { get; set; }
}

[Table(Name = "it_rt_bin", Schema = "public")]
public sealed class BinaryStorageRow
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    // A Guid stored as bytea rather than uuid, via the storage override.
    [Column(Name = "gid", StorageType = StorageType.Binary)] public Guid Gid { get; set; }
}

[Collection("codelogic")]
public sealed class LiveRoundTripTests
{
    private const string Gate = "CL_PG_TEST_HOST";
    private const string Reason = "set CL_PG_TEST_HOST (+ _PORT/_DB/_USER/_PASS) to run live PostgreSQL tests";

    private readonly PostgreSQLRuntimeFixture _fx;
    private PostgreSQLLibrary Lib => _fx.Library ?? throw new InvalidOperationException("Runtime not booted.");

    public LiveRoundTripTests(PostgreSQLRuntimeFixture fx) => _fx = fx;

    [FactRequiresEnv(Gate, Reason)]
    public async Task Every_mainstream_type_survives_a_round_trip()
    {
        var lib = Lib;
        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_rt_all\" CASCADE");
        var sync = await lib.SyncTableAsync<RoundTripRow>(createBackup: false);
        Assert.True(sync.IsSuccess, sync.Error?.ToString());

        var original = new RoundTripRow
        {
            CShort = -12345,
            CInt = int.MinValue,
            CLong = long.MaxValue,
            CFloat = 1.25f,
            CDouble = Math.PI,
            CDecimal = 12345.6789m,
            CString = "unicode: æøå 日本語 'quoted' \"dquoted\"",
            CText = new string('x', 5000),
            CBool = true,
            CGuid = Guid.NewGuid(),
            CDateTime = new DateTime(2026, 3, 14, 15, 9, 26, DateTimeKind.Utc),
            CDateTimeOffset = new DateTimeOffset(2026, 3, 14, 15, 9, 26, TimeSpan.Zero),
            CDate = new DateOnly(2026, 3, 14),
            CTime = new TimeOnly(15, 9, 26),
            CSpan = TimeSpan.FromMinutes(93),
            CBytes = [0x00, 0x01, 0xFE, 0xFF],
            CJsonb = """{"a":1,"b":[true,null,"x"]}""",
            CEnum = DayOfWeek.Thursday,
            CInts = [1, 2, 3],
            CStrings = ["a", "b"],
            NInt = null,
            NString = null,
            NGuid = null,
            NDateTime = null,
        };

        var repo = lib.GetRepository<RoundTripRow>();
        var inserted = await repo.InsertAsync(original);
        Assert.True(inserted.IsSuccess, inserted.Error?.ToString());

        var fetched = await repo.GetByIdAsync(inserted.Value!.Id);
        Assert.True(fetched.IsSuccess, fetched.Error?.ToString());
        var r = fetched.Value!;

        Assert.Equal(original.CShort, r.CShort);
        Assert.Equal(original.CInt, r.CInt);
        Assert.Equal(original.CLong, r.CLong);
        Assert.Equal(original.CFloat, r.CFloat);
        Assert.Equal(original.CDouble, r.CDouble);
        Assert.Equal(original.CDecimal, r.CDecimal);
        Assert.Equal(original.CString, r.CString);
        Assert.Equal(original.CText, r.CText);
        Assert.Equal(original.CBool, r.CBool);
        Assert.Equal(original.CGuid, r.CGuid);
        Assert.Equal(original.CDateTime, r.CDateTime);
        Assert.Equal(original.CDateTimeOffset.UtcDateTime, r.CDateTimeOffset.UtcDateTime);
        Assert.Equal(original.CDate, r.CDate);
        Assert.Equal(original.CTime, r.CTime);
        Assert.Equal(original.CSpan, r.CSpan);
        Assert.Equal(original.CBytes, r.CBytes);
        Assert.Equal(original.CEnum, r.CEnum);
        Assert.Equal(original.CInts, r.CInts);
        Assert.Equal(original.CStrings, r.CStrings);

        // jsonb normalises whitespace and key order; compare semantically.
        Assert.Equal(
            System.Text.Json.JsonDocument.Parse(original.CJsonb).RootElement.GetRawText().Length > 0,
            System.Text.Json.JsonDocument.Parse(r.CJsonb).RootElement.GetRawText().Length > 0);

        Assert.Null(r.NInt);
        Assert.Null(r.NString);
        Assert.Null(r.NGuid);
        Assert.Null(r.NDateTime);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task Unspecified_datetime_kind_is_treated_as_utc()
    {
        var lib = Lib;
        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_rt_all\" CASCADE");
        await lib.SyncTableAsync<RoundTripRow>(createBackup: false);

        // Npgsql refuses a Local/Unspecified DateTime for timestamptz; the converter
        // normalises Unspecified to UTC rather than letting the write fail.
        var unspecified = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified);
        var repo = lib.GetRepository<RoundTripRow>();
        var inserted = await repo.InsertAsync(new RoundTripRow { CDateTime = unspecified, CString = "k" });
        Assert.True(inserted.IsSuccess, inserted.Error?.ToString());

        var back = await repo.GetByIdAsync(inserted.Value!.Id);
        Assert.Equal(DateTimeKind.Utc, back.Value!.CDateTime.Kind);
        Assert.Equal(unspecified.Ticks, back.Value.CDateTime.Ticks);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task Local_datetime_kind_is_converted_not_rejected()
    {
        var lib = Lib;
        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_rt_all\" CASCADE");
        await lib.SyncTableAsync<RoundTripRow>(createBackup: false);

        var local = DateTime.SpecifyKind(new DateTime(2026, 6, 1, 12, 0, 0), DateTimeKind.Local);
        var repo = lib.GetRepository<RoundTripRow>();
        var inserted = await repo.InsertAsync(new RoundTripRow { CDateTime = local, CString = "l" });
        Assert.True(inserted.IsSuccess, inserted.Error?.ToString());

        var back = await repo.GetByIdAsync(inserted.Value!.Id);
        Assert.Equal(local.ToUniversalTime(), back.Value!.CDateTime);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task Guid_stored_as_bytea_round_trips()
    {
        var lib = Lib;
        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_rt_bin\" CASCADE");
        var sync = await lib.SyncTableAsync<BinaryStorageRow>(createBackup: false);
        Assert.True(sync.IsSuccess, sync.Error?.ToString());

        var stored = await lib.SqlScalarAsync<string>("""
            SELECT pg_catalog.format_type(a.atttypid, a.atttypmod)
            FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname='public' AND c.relname='it_rt_bin' AND a.attname='gid'
            """);
        Assert.Equal("bytea", stored.Value);

        var id = Guid.NewGuid();
        var repo = lib.GetRepository<BinaryStorageRow>();
        var inserted = await repo.InsertAsync(new BinaryStorageRow { Gid = id });
        Assert.True(inserted.IsSuccess, inserted.Error?.ToString());

        var back = await repo.GetByIdAsync(inserted.Value!.Id);
        Assert.True(back.IsSuccess, back.Error?.ToString());
        Assert.Equal(id, back.Value!.Gid);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task Inet_round_trips()
    {
        var lib = Lib;
        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS public.it_rt_inet CASCADE");
        await lib.ExecuteSqlAsync("CREATE TABLE public.it_rt_inet (id bigint, addr inet)");
        await lib.ExecuteSqlAsync("INSERT INTO public.it_rt_inet VALUES (1, @a)",
            new Dictionary<string, object?> { ["@a"] = IPAddress.Parse("192.168.1.10") });

        var rows = await lib.SqlQueryAsync<InetRow>("SELECT id AS \"Id\", addr AS \"Addr\" FROM public.it_rt_inet");
        Assert.True(rows.IsSuccess, rows.Error?.ToString());
        Assert.Equal(IPAddress.Parse("192.168.1.10"), rows.Value![0].Addr);
    }

    /// <summary>
    /// Values that filter must bind as the column's own type. A predicate over a uuid or a
    /// timestamptz column is where a mis-typed parameter shows up as "operator does not
    /// exist" rather than as a wrong answer.
    /// </summary>
    [FactRequiresEnv(Gate, Reason)]
    public async Task Typed_columns_filter_correctly()
    {
        var lib = Lib;
        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_rt_all\" CASCADE");
        await lib.SyncTableAsync<RoundTripRow>(createBackup: false);

        var target = Guid.NewGuid();
        var cutoff = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var repo = lib.GetRepository<RoundTripRow>();
        await repo.InsertAsync(new RoundTripRow { CGuid = target, CString = "hit", CDateTime = cutoff.AddDays(1), CDecimal = 5m });
        await repo.InsertAsync(new RoundTripRow { CGuid = Guid.NewGuid(), CString = "miss", CDateTime = cutoff.AddDays(-1), CDecimal = 50m });

        var byGuid = await lib.Query<RoundTripRow>().Where(r => r.CGuid == target).ToListAsync();
        Assert.True(byGuid.IsSuccess, byGuid.Error?.ToString());
        Assert.Single(byGuid.Value!);
        Assert.Equal("hit", byGuid.Value![0].CString);

        var byDate = await lib.Query<RoundTripRow>().Where(r => r.CDateTime > cutoff).ToListAsync();
        Assert.True(byDate.IsSuccess, byDate.Error?.ToString());
        Assert.Single(byDate.Value!);

        var byDecimal = await lib.Query<RoundTripRow>().Where(r => r.CDecimal > 10m).ToListAsync();
        Assert.True(byDecimal.IsSuccess, byDecimal.Error?.ToString());
        Assert.Single(byDecimal.Value!);
        Assert.Equal("miss", byDecimal.Value![0].CString);
    }

    public sealed class InetRow
    {
        [Column(Name = "Id")] public long Id { get; set; }
        [Column(Name = "Addr")] public IPAddress? Addr { get; set; }
    }
}
