using System.ComponentModel;

namespace CL.MSSQL.Core;

/// <summary>
/// Marker methods recognized by the expression translator and rewritten into SQL Server
/// function calls. Calls to these members are never executed at runtime in a query
/// context — the translator peels them out of the expression tree. Invoking them
/// directly outside a query throws, matching the pattern used by EF Core's
/// <c>EF.Functions</c>.
/// </summary>
public static class SqlFn
{
    // ── Date/time ─────────────────────────────────────────────────────────────

    /// <summary>SQL Server <c>DATEPART(year, d)</c>.</summary>
    public static int Year(DateTime d) => throw OutsideQuery(nameof(Year));
    /// <summary>SQL Server <c>DATEPART(month, d)</c>.</summary>
    public static int Month(DateTime d) => throw OutsideQuery(nameof(Month));
    /// <summary>SQL Server <c>DATEPART(day, d)</c>.</summary>
    public static int Day(DateTime d) => throw OutsideQuery(nameof(Day));
    /// <summary>SQL Server <c>DATEPART(hour, d)</c>.</summary>
    public static int Hour(DateTime d) => throw OutsideQuery(nameof(Hour));
    /// <summary>SQL Server <c>DATEPART(minute, d)</c>.</summary>
    public static int Minute(DateTime d) => throw OutsideQuery(nameof(Minute));

    /// <summary>
    /// Day of week, matching .NET's <c>DayOfWeek</c> numbering (0 = Sunday … 6 = Saturday).
    /// <c>DATEPART(weekday, d)</c> is deliberately not used: its result shifts with the
    /// session's <c>SET DATEFIRST</c>. The translator counts days from a known Sunday instead,
    /// so the value is the same on any connection.
    /// </summary>
    public static int DayOfWeek(DateTime d) => throw OutsideQuery(nameof(DayOfWeek));

    /// <summary>SQL Server <c>CONVERT(date, d)</c> — strips the time component.</summary>
    public static DateTime Date(DateTime d) => throw OutsideQuery(nameof(Date));

    /// <summary>
    /// Rounds <paramref name="d"/> down to the nearest <paramref name="seconds"/>-wide
    /// bucket, by flooring the epoch-second offset with <c>DATEDIFF_BIG</c> and adding it
    /// back with <c>DATEADD</c>. Use for time-series bucketing in <c>GroupBy</c> keys.
    /// </summary>
    public static DateTime BucketUtc(DateTime d, int seconds) => throw OutsideQuery(nameof(BucketUtc));

    // ── Conditional / null ────────────────────────────────────────────────────

    /// <summary>SQL Server <c>COALESCE(a, b, ...)</c>.</summary>
    public static T Coalesce<T>(params T[] values) => throw OutsideQuery(nameof(Coalesce));
    /// <summary>Substitutes <paramref name="fallback"/> for a null <paramref name="value"/>.
    /// T-SQL has no <c>IFNULL</c>, so this emits <c>COALESCE(v, fallback)</c>.</summary>
    public static T IfNull<T>(T value, T fallback) => throw OutsideQuery(nameof(IfNull));

    // ── String ────────────────────────────────────────────────────────────────

    /// <summary>SQL Server <c>LOWER(s)</c>.</summary>
    public static string Lower(string s) => throw OutsideQuery(nameof(Lower));
    /// <summary>SQL Server <c>UPPER(s)</c>.</summary>
    public static string Upper(string s) => throw OutsideQuery(nameof(Upper));
    /// <summary>SQL Server <c>CONCAT(a, b, …)</c>.</summary>
    public static string Concat(params string[] parts) => throw OutsideQuery(nameof(Concat));
    /// <summary>
    /// SQL Server <c>s LIKE pattern</c>. Unlike the <c>Contains</c>/<c>StartsWith</c>/<c>EndsWith</c>
    /// visitor shortcuts, this passes <paramref name="pattern"/> through untouched — the
    /// caller supplies <c>%</c>/<c>_</c> wildcards directly.
    /// <para>
    /// T-SQL has no boolean expression type, so in a key or projection the predicate is
    /// materialized as <c>CAST(CASE WHEN s LIKE pattern THEN 1 ELSE 0 END AS bit)</c>.
    /// </para>
    /// </summary>
    public static bool Like(string s, string pattern) => throw OutsideQuery(nameof(Like));

    // ── Math ──────────────────────────────────────────────────────────────────

    /// <summary>SQL Server <c>ROUND(v, digits)</c>.</summary>
    public static double Round(double v, int digits) => throw OutsideQuery(nameof(Round));
    /// <summary>SQL Server <c>FLOOR(v)</c>.</summary>
    public static double Floor(double v) => throw OutsideQuery(nameof(Floor));
    /// <summary>SQL Server <c>CEILING(v)</c>.</summary>
    public static double Ceiling(double v) => throw OutsideQuery(nameof(Ceiling));

    [EditorBrowsable(EditorBrowsableState.Never)]
    private static InvalidOperationException OutsideQuery(string name) =>
        new($"SqlFn.{name} cannot be invoked outside a CL.MSSQL query expression. " +
            "These methods are markers rewritten to SQL by the expression translator.");
}
