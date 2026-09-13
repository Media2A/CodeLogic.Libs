using CL.PostgreSQL;
using CL.PostgreSQL.Core;
using CL.PostgreSQL.Models;
using Xunit;
using Xunit.Abstractions;

namespace PostgreSQL.Tests;

/// <summary>
/// Every <see cref="DataType"/> the library can emit, checked against a real server.
/// <para>
/// The DDL type strings are hand-written, and an invalid one fails only when PostgreSQL
/// parses it — exactly the class of defect the offline suite cannot see. This walks the
/// whole enum rather than the handful of types the feature tests happen to use.
/// </para>
/// </summary>
[Collection("codelogic")]
public sealed class LiveTypeMatrixTests
{
    private const string Gate = "CL_PG_TEST_HOST";
    private const string Reason = "set CL_PG_TEST_HOST (+ _PORT/_DB/_USER/_PASS) to run live PostgreSQL tests";

    private readonly PostgreSQLRuntimeFixture _fx;
    private readonly ITestOutputHelper _out;
    private PostgreSQLLibrary Lib => _fx.Library ?? throw new InvalidOperationException("Runtime not booted.");

    public LiveTypeMatrixTests(PostgreSQLRuntimeFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _out = output;
    }

    /// <summary>
    /// Emits every DataType as a column and asks PostgreSQL to accept it. A type string the
    /// server rejects is a bug regardless of whether any entity currently uses that type.
    /// </summary>
    [FactRequiresEnv(Gate, Reason)]
    public async Task Every_datatype_produces_a_type_postgres_accepts()
    {
        var lib = Lib;
        var failures = new List<string>();

        foreach (var dataType in Enum.GetValues<DataType>())
        {
            if (dataType == DataType.Unspecified) continue;   // resolved from the CLR type, never emitted

            // Sizes/precision are supplied so the parameterised types render fully.
            var column = new ColumnAttribute { DataType = dataType, Size = 12, Precision = 10, Scale = 2 };
            var ddl = TypeConverter.GetPostgreSqlType(column, StorageType.Default, null);

            var table = $"it_type_{dataType.ToString().ToLowerInvariant()}";
            await lib.ExecuteSqlAsync($"DROP TABLE IF EXISTS public.\"{table}\"");
            var create = await lib.ExecuteSqlAsync($"CREATE TABLE public.\"{table}\" (c {ddl})");

            if (create.IsFailure)
                failures.Add($"{dataType,-16} -> \"{ddl}\"  :: {create.Error?.Message}");
            else
                await lib.ExecuteSqlAsync($"DROP TABLE public.\"{table}\"");
        }

        foreach (var f in failures) _out.WriteLine(f);
        Assert.True(failures.Count == 0,
            $"{failures.Count} DataType(s) produced SQL PostgreSQL rejected:\n" + string.Join("\n", failures));
    }

    /// <summary>
    /// The inferred mapping for every CLR type the library claims to support, checked the
    /// same way. Inference is the common path — most entities declare no DataType at all.
    /// </summary>
    [FactRequiresEnv(Gate, Reason)]
    public async Task Every_inferred_clr_type_produces_a_type_postgres_accepts()
    {
        var lib = Lib;
        Type[] clrTypes =
        [
            typeof(bool), typeof(byte), typeof(sbyte), typeof(short), typeof(ushort),
            typeof(int), typeof(uint), typeof(long), typeof(ulong),
            typeof(float), typeof(double), typeof(decimal),
            typeof(string), typeof(char), typeof(Guid),
            typeof(DateTime), typeof(DateTimeOffset), typeof(DateOnly), typeof(TimeOnly), typeof(TimeSpan),
            typeof(byte[]), typeof(System.Net.IPAddress),
            typeof(short[]), typeof(int[]), typeof(long[]), typeof(string[]),
            typeof(decimal[]), typeof(Guid[]), typeof(bool[]),
            typeof(DayOfWeek),   // enum
        ];

        var failures = new List<string>();
        foreach (var clr in clrTypes)
        {
            var inferred = TypeConverter.InferColumn(clr);
            var ddl = TypeConverter.GetPostgreSqlType(inferred, StorageType.Default, clr);

            var table = "it_infer_probe";
            await lib.ExecuteSqlAsync($"DROP TABLE IF EXISTS public.\"{table}\"");
            var create = await lib.ExecuteSqlAsync($"CREATE TABLE public.\"{table}\" (c {ddl})");

            if (create.IsFailure)
                failures.Add($"{clr.Name,-18} -> \"{ddl}\"  :: {create.Error?.Message}");
            else
                await lib.ExecuteSqlAsync($"DROP TABLE public.\"{table}\"");
        }

        foreach (var f in failures) _out.WriteLine(f);
        Assert.True(failures.Count == 0,
            $"{failures.Count} CLR type(s) inferred SQL PostgreSQL rejected:\n" + string.Join("\n", failures));
    }

    /// <summary>
    /// Binary storage overrides must also produce a valid type, for every CLR type that can
    /// be serialised into one.
    /// </summary>
    [FactRequiresEnv(Gate, Reason)]
    public async Task Binary_storage_override_produces_bytea()
    {
        var lib = Lib;
        foreach (var storage in new[] { StorageType.Binary, StorageType.VarBinary })
        {
            var ddl = TypeConverter.GetPostgreSqlType(new ColumnAttribute(), storage, typeof(Guid));
            Assert.Equal("bytea", ddl);

            await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS public.it_bin_probe");
            var create = await lib.ExecuteSqlAsync($"CREATE TABLE public.it_bin_probe (c {ddl})");
            Assert.True(create.IsSuccess, create.Error?.ToString());
            await lib.ExecuteSqlAsync("DROP TABLE public.it_bin_probe");
        }
    }
}
