using System.Collections;
using System.Net;
using System.Net.NetworkInformation;
using CL.PostgreSQL;
using CL.PostgreSQL.Models;
using NpgsqlTypes;
using Xunit;
using Xunit.Abstractions;

namespace PostgreSQL.Tests;

// The PostgreSQL-only types the library declares but the mainstream round-trip test does
// not reach: ranges, bit strings, network addresses, xml, money, and the serial
// pseudo-types. The DDL for all of these is already proven valid; what is unproven is
// whether a value can go in and come back as the right CLR type.

[Table(Name = "it_exotic", Schema = "public")]
public sealed class ExoticRow
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }

    [Column(Name = "r_int4", DataType = DataType.Int4Range)] public NpgsqlRange<int> RInt4 { get; set; }
    [Column(Name = "r_int8", DataType = DataType.Int8Range)] public NpgsqlRange<long> RInt8 { get; set; }
    [Column(Name = "r_num", DataType = DataType.NumRange)] public NpgsqlRange<decimal> RNum { get; set; }
    [Column(Name = "r_date", DataType = DataType.DateRange)] public NpgsqlRange<DateOnly> RDate { get; set; }

    [Column(Name = "b_fixed", DataType = DataType.Bit, Size = 8)] public BitArray BFixed { get; set; } = new(8);
    [Column(Name = "b_var", DataType = DataType.VarBit, Size = 16)] public BitArray BVar { get; set; } = new(0);

    [Column(Name = "n_mac", DataType = DataType.MacAddr)] public PhysicalAddress? NMac { get; set; }
    [Column(Name = "n_inet", DataType = DataType.Inet)] public IPAddress? NInet { get; set; }

    [Column(Name = "x_doc", DataType = DataType.Xml)] public string? XDoc { get; set; }
    [Column(Name = "m_amount", DataType = DataType.Money)] public decimal MAmount { get; set; }
    [Column(Name = "i_span", DataType = DataType.Interval)] public TimeSpan ISpan { get; set; }
}

[Collection("codelogic")]
public sealed class LiveExoticTypeTests
{
    private const string Gate = "CL_PG_TEST_HOST";
    private const string Reason = "set CL_PG_TEST_HOST (+ _PORT/_DB/_USER/_PASS) to run live PostgreSQL tests";

    private readonly PostgreSQLRuntimeFixture _fx;
    private readonly ITestOutputHelper _out;
    private PostgreSQLLibrary Lib => _fx.Library ?? throw new InvalidOperationException("Runtime not booted.");

    public LiveExoticTypeTests(PostgreSQLRuntimeFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _out = output;
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task Exotic_types_round_trip()
    {
        var lib = Lib;
        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_exotic\" CASCADE");
        var sync = await lib.SyncTableAsync<ExoticRow>(createBackup: false);
        Assert.True(sync.IsSuccess, sync.Error?.ToString());

        var bFixed = new BitArray([true, false, true, false, true, false, true, false]);
        var bVar = new BitArray([true, true, false]);

        var original = new ExoticRow
        {
            RInt4 = new NpgsqlRange<int>(1, true, 10, false),
            RInt8 = new NpgsqlRange<long>(100L, true, 200L, false),
            RNum = new NpgsqlRange<decimal>(1.5m, true, 9.5m, false),
            RDate = new NpgsqlRange<DateOnly>(new DateOnly(2026, 1, 1), true, new DateOnly(2026, 2, 1), false),
            BFixed = bFixed,
            BVar = bVar,
            NMac = PhysicalAddress.Parse("00-1A-2B-3C-4D-5E"),
            NInet = IPAddress.Parse("10.0.0.7"),
            XDoc = "<root><a>1</a></root>",
            MAmount = 1234.56m,
            ISpan = TimeSpan.FromMinutes(150),
        };

        var repo = lib.GetRepository<ExoticRow>();
        var inserted = await repo.InsertAsync(original);
        Assert.True(inserted.IsSuccess, inserted.Error?.ToString());

        var back = await repo.GetByIdAsync(inserted.Value!.Id);
        Assert.True(back.IsSuccess, back.Error?.ToString());
        var r = back.Value!;

        Assert.Equal(original.RInt4, r.RInt4);
        Assert.Equal(original.RInt8, r.RInt8);
        Assert.Equal(original.RNum, r.RNum);
        Assert.Equal(original.RDate, r.RDate);
        Assert.Equal(ToBits(bFixed), ToBits(r.BFixed));
        Assert.Equal(ToBits(bVar), ToBits(r.BVar));
        Assert.Equal(original.NMac, r.NMac);
        Assert.Equal(original.NInet, r.NInet);
        Assert.Contains("<a>1</a>", r.XDoc);
        Assert.Equal(original.MAmount, r.MAmount);
        Assert.Equal(original.ISpan, r.ISpan);
    }

    /// <summary>
    /// Ranges must also work as filter values, since that is the point of having them.
    /// </summary>
    [FactRequiresEnv(Gate, Reason)]
    public async Task Range_containment_filters_server_side()
    {
        var lib = Lib;
        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_exotic\" CASCADE");
        await lib.SyncTableAsync<ExoticRow>(createBackup: false);

        var repo = lib.GetRepository<ExoticRow>();
        await repo.InsertAsync(new ExoticRow { RInt4 = new NpgsqlRange<int>(1, true, 10, false) });
        await repo.InsertAsync(new ExoticRow { RInt4 = new NpgsqlRange<int>(50, true, 60, false) });

        // The containment operator has no LINQ spelling; raw SQL is the supported route.
        var hit = await lib.SqlScalarAsync<long>(
            "SELECT count(*) FROM public.it_exotic WHERE r_int4 @> @probe",
            new Dictionary<string, object?> { ["@probe"] = 5 });
        Assert.True(hit.IsSuccess, hit.Error?.ToString());
        Assert.Equal(1, hit.Value);
    }

    /// <summary>
    /// serial / bigserial are legacy pseudo-types: PostgreSQL rewrites them to an integer
    /// column with a sequence-backed default. The library prefers identity columns, but the
    /// DataType members exist, so the DDL they generate must still behave.
    /// </summary>
    [FactRequiresEnv(Gate, Reason)]
    public async Task Serial_pseudo_types_create_sequence_backed_columns()
    {
        var lib = Lib;
        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS public.it_serial CASCADE");
        await lib.ExecuteSqlAsync("CREATE TABLE public.it_serial (a smallserial, b serial, c bigserial)");

        var types = await lib.SqlQueryAsync<NameType>("""
            SELECT a.attname AS "Name", pg_catalog.format_type(a.atttypid, a.atttypmod) AS "Type"
            FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname='public' AND c.relname='it_serial' AND a.attnum > 0 AND NOT a.attisdropped
            ORDER BY a.attnum
            """);
        Assert.True(types.IsSuccess, types.Error?.ToString());

        // The serial types resolve to their underlying integer widths.
        var map = types.Value!.ToDictionary(t => t.Name, t => t.Type, StringComparer.Ordinal);
        Assert.Equal("smallint", map["a"]);
        Assert.Equal("integer", map["b"]);
        Assert.Equal("bigint", map["c"]);

        // And the sequence default actually assigns values.
        await lib.ExecuteSqlAsync("INSERT INTO public.it_serial DEFAULT VALUES");
        var first = await lib.SqlScalarAsync<int>("SELECT b FROM public.it_serial LIMIT 1");
        Assert.Equal(1, first.Value);

        await lib.ExecuteSqlAsync("DROP TABLE public.it_serial");
    }

    private static string ToBits(BitArray bits)
    {
        var chars = new char[bits.Length];
        for (var i = 0; i < bits.Length; i++) chars[i] = bits[i] ? '1' : '0';
        return new string(chars);
    }

    public sealed class NameType
    {
        [Column(Name = "Name")] public string Name { get; set; } = "";
        [Column(Name = "Type")] public string Type { get; set; } = "";
    }
}
