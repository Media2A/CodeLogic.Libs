using System.Reflection;
using CL.SQLite;
using CL.SQLite.Localization;
using CL.SQLite.Models;
using CL.SQLite.Services;
using CodeLogic.Framework.Libraries;
using Xunit;

namespace SQLite.Tests;

/// <summary>
/// The attribute surface, the localization strings, and the two library members that no other
/// test file names directly. Nothing here needs the CodeLogic runtime.
/// </summary>
public sealed class AttributeAndStringsTests
{
    // ── SQLiteTableAttribute ──────────────────────────────────────────────────

    [Fact]
    public void SQLiteTableAttribute_carries_the_table_name_and_is_not_inherited()
    {
        var attr = typeof(SyncAlpha).GetCustomAttribute<SQLiteTableAttribute>()!;
        Assert.Equal("sync_alpha", attr.TableName);

        var usage = typeof(SQLiteTableAttribute).GetCustomAttribute<AttributeUsageAttribute>()!;
        Assert.Equal(AttributeTargets.Class, usage.ValidOn);
        Assert.False(usage.Inherited);
        Assert.False(usage.AllowMultiple);
    }

    // ── SQLiteColumnAttribute ─────────────────────────────────────────────────

    [Fact]
    public void SQLiteColumnAttribute_defaults_every_flag_to_off()
    {
        var attr = new SQLiteColumnAttribute();

        Assert.False(attr.IsPrimaryKey);
        Assert.False(attr.IsIndexed);
        Assert.False(attr.IsUnique);
        Assert.False(attr.IsAutoIncrement);
        Assert.False(attr.IsNotNull);
        Assert.Null(attr.ColumnName);
        Assert.Null(attr.DefaultValue);
        Assert.Equal(0, attr.Size);
        Assert.Equal(SQLiteDataType.INTEGER, attr.DataType); // the zero value of the enum
    }

    [Fact]
    public void SQLiteColumnAttribute_round_trips_every_option_it_declares()
    {
        var attr = new SQLiteColumnAttribute
        {
            IsPrimaryKey = true,
            IsIndexed = true,
            IsUnique = true,
            IsAutoIncrement = true,
            ColumnName = "c",
            DataType = SQLiteDataType.BLOB,
            Size = 128,
            IsNotNull = true,
            DefaultValue = "'x'"
        };

        Assert.True(attr.IsPrimaryKey && attr.IsIndexed && attr.IsUnique && attr.IsAutoIncrement && attr.IsNotNull);
        Assert.Equal("c", attr.ColumnName);
        Assert.Equal(SQLiteDataType.BLOB, attr.DataType);
        Assert.Equal(128, attr.Size);
        Assert.Equal("'x'", attr.DefaultValue);
    }

    // ── SQLiteIndexAttribute ──────────────────────────────────────────────────

    [Fact]
    public void SQLiteIndexAttribute_keeps_its_Columns_in_the_declared_order()
    {
        var attr = new SQLiteIndexAttribute("a", "b", "c");

        Assert.Equal(["a", "b", "c"], attr.Columns);
        Assert.False(attr.IsUnique);
        Assert.Null(attr.Name);
    }

    [Fact]
    public void SQLiteIndexAttribute_is_repeatable_on_a_class_and_both_copies_are_read()
    {
        var attrs = typeof(SyncIndexed).GetCustomAttributes<SQLiteIndexAttribute>().ToList();

        Assert.Equal(2, attrs.Count);
        var named = attrs.Single(a => a.Name == "ix_sync_named");
        Assert.Equal(["a", "b"], named.Columns);
        Assert.False(named.IsUnique);

        var unique = attrs.Single(a => a.Name is null);
        Assert.Equal(["c"], unique.Columns);
        Assert.True(unique.IsUnique);

        var usage = typeof(SQLiteIndexAttribute).GetCustomAttribute<AttributeUsageAttribute>()!;
        Assert.True(usage.AllowMultiple);
    }

    [Fact]
    public void SQLiteIndexAttribute_accepts_a_single_column()
    {
        var attr = new SQLiteIndexAttribute("only") { IsUnique = true, Name = "ix" };

        Assert.Equal(["only"], attr.Columns);
        Assert.True(attr.IsUnique);
        Assert.Equal("ix", attr.Name);
    }

    // ── SQLiteForeignKeyAttribute ─────────────────────────────────────────────

    [Fact]
    public void SQLiteForeignKeyAttribute_defaults_both_actions_to_NoAction()
    {
        var attr = new SQLiteForeignKeyAttribute("parent", "id");

        Assert.Equal("parent", attr.ReferencedTable);
        Assert.Equal("id", attr.ReferencedColumn);
        Assert.Equal(ForeignKeyAction.NoAction, attr.OnDelete);
        Assert.Equal(ForeignKeyAction.NoAction, attr.OnUpdate);
    }

    [Fact]
    public void SQLiteForeignKeyAttribute_reads_back_off_a_property()
    {
        var attr = typeof(SyncFkChild).GetProperty(nameof(SyncFkChild.ParentId))!
            .GetCustomAttribute<SQLiteForeignKeyAttribute>()!;

        Assert.Equal("sync_fk_parent", attr.ReferencedTable);
        Assert.Equal("id", attr.ReferencedColumn);
        Assert.Equal(ForeignKeyAction.Cascade, attr.OnDelete);
        Assert.Equal(ForeignKeyAction.NoAction, attr.OnUpdate);
    }

    // ── Localization ──────────────────────────────────────────────────────────

    [Fact]
    public void SQLiteStrings_supplies_a_non_empty_default_for_every_declared_string()
    {
        var strings = new SQLiteStrings();
        var props = typeof(SQLiteStrings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string) && p.CanWrite)
            .ToList();

        Assert.NotEmpty(props);
        Assert.All(props, p => Assert.False(string.IsNullOrWhiteSpace((string?)p.GetValue(strings))));
    }

    [Fact]
    public void The_SQLiteStrings_format_placeholders_match_how_the_library_uses_them()
    {
        var s = new SQLiteStrings();

        // Single-argument formats used by SQLiteLibrary and the services.
        Assert.Contains("{0}", s.LibraryInitialized);
        Assert.Contains("{0}", s.HealthCheckPassed);
        Assert.Contains("{0}", s.HealthCheckFailed);
        Assert.Contains("{0}", s.TableCreated);
        Assert.Contains("{0}", s.TableSynced);
        Assert.Contains("{0}", s.ConnectionCreated);
        Assert.Contains("{0}", s.TableSyncStarted);

        // Two-argument formats.
        Assert.Contains("{1}", s.TableSyncFailed);
        Assert.Contains("{1}", s.SlowQueryDetected);

        // No-argument strings.
        Assert.DoesNotContain("{0}", s.LibraryStarted);
        Assert.DoesNotContain("{0}", s.LibraryStopped);
        Assert.DoesNotContain("{0}", s.ConnectionReused);
        Assert.DoesNotContain("{0}", s.ConnectionReleased);
    }

    // ── Library lifecycle members ─────────────────────────────────────────────

    /// <summary>
    /// The four lifecycle phases cannot be driven directly from a test: three of them take a
    /// <see cref="LibraryContext"/>, which the framework constructs and does not expose a
    /// public constructor for, and the CodeLogic runtime is a process-wide singleton that the
    /// shared fixture has already booted. What can be asserted is the shape of the contract —
    /// that all four are public and reachable — with their effects covered through the booted
    /// fixture in <see cref="TableSyncAndLifecycleTests"/>.
    /// </summary>
    [Fact]
    public void All_four_lifecycle_phases_are_part_of_the_public_contract()
    {
        var t = typeof(SQLiteLibrary);

        var configure = t.GetMethod(nameof(SQLiteLibrary.OnConfigureAsync))!;
        var initialize = t.GetMethod(nameof(SQLiteLibrary.OnInitializeAsync))!;
        var start = t.GetMethod(nameof(SQLiteLibrary.OnStartAsync))!;
        var stop = t.GetMethod(nameof(SQLiteLibrary.OnStopAsync))!;

        foreach (var phase in new[] { configure, initialize, start })
        {
            Assert.True(phase.IsPublic);
            Assert.Equal(typeof(Task), phase.ReturnType);
            Assert.Equal(typeof(LibraryContext), Assert.Single(phase.GetParameters()).ParameterType);
        }

        Assert.True(stop.IsPublic);
        Assert.Empty(stop.GetParameters());
        Assert.Equal(typeof(Task), stop.ReturnType);
    }

    /// <summary>
    /// Confirms the limitation recorded above rather than working around it: every
    /// context-taking phase dereferences <c>context.Logger</c> on its first line, so none of
    /// them can be driven with a stand-in. Their behaviour is only reachable through a real
    /// framework boot, which the shared fixture performs once for the whole assembly.
    /// </summary>
    [Fact]
    public async Task The_context_taking_phases_cannot_be_driven_without_a_real_LibraryContext()
    {
        var fresh = new SQLiteLibrary();

        await Assert.ThrowsAsync<NullReferenceException>(() => fresh.OnStartAsync(null!));
        await Assert.ThrowsAsync<NullReferenceException>(() => fresh.OnConfigureAsync(null!));
        await Assert.ThrowsAsync<NullReferenceException>(() => fresh.OnInitializeAsync(null!));

        // OnStopAsync takes no context and is safe on a library that never started.
        await fresh.OnStopAsync();
    }

    // ── WhereClauseBuilder (internal, and unreferenced by the library) ───────

    /// <summary>
    /// <c>WhereClauseBuilder.Build</c> is internal and has no caller anywhere in CL.SQLite —
    /// the query path goes through <c>SQLiteExpressionVisitor</c> instead. It is reached here
    /// by reflection so its behaviour is at least pinned while it remains in the tree.
    /// </summary>
    [Fact]
    public void WhereClauseBuilder_Build_emits_quoted_columns_indexed_parameters_and_null_forms()
    {
        var (clause, parameters) = InvokeBuild(
        [
            new WhereCondition { Column = "Order", Operator = "=", Value = 1 },
            new WhereCondition { Column = "note", Operator = "=", Value = null },
            new WhereCondition { Column = "other", Operator = "!=", Value = null, LogicalOperator = "OR" },
            new WhereCondition { Column = "n", Operator = ">", Value = 5, LogicalOperator = "OR" }
        ]);

        Assert.Equal(" WHERE \"Order\" = @p0 AND \"note\" IS NULL OR \"other\" IS NOT NULL OR \"n\" > @p3", clause);
        Assert.Equal(2, parameters.Count);
        Assert.Equal(1, parameters["@p0"]);
        Assert.Equal(5, parameters["@p3"]);
    }

    [Fact]
    public void WhereClauseBuilder_Build_returns_an_empty_clause_for_no_conditions()
    {
        var (clause, parameters) = InvokeBuild([]);

        Assert.Equal(string.Empty, clause);
        Assert.Empty(parameters);
    }

    /// <summary>
    /// The parameter index advances for every condition, including the NULL ones that never
    /// bind a parameter, so the emitted names are sparse (@p0 then @p3 above). Harmless on its
    /// own, but it means the names are not a contiguous sequence.
    /// </summary>
    [Fact]
    public void WhereClauseBuilder_leaves_gaps_in_its_parameter_numbering()
    {
        var (_, parameters) = InvokeBuild(
        [
            new WhereCondition { Column = "a", Operator = "=", Value = null },
            new WhereCondition { Column = "b", Operator = "=", Value = 2 }
        ]);

        Assert.Equal(["@p1"], parameters.Keys);
    }

    private static (string Clause, Dictionary<string, object?> Parameters) InvokeBuild(
        List<WhereCondition> conditions)
    {
        var type = typeof(TableSyncService).Assembly
            .GetType("CL.SQLite.Services.WhereClauseBuilder", throwOnError: true)!;
        var method = type.GetMethod("Build", BindingFlags.Public | BindingFlags.Static)!;
        var result = method.Invoke(null, [conditions])!;

        return ((string)result.GetType().GetField("Item1")!.GetValue(result)!,
                (Dictionary<string, object?>)result.GetType().GetField("Item2")!.GetValue(result)!);
    }
}
