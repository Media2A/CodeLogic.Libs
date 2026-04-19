using System.ComponentModel;

namespace CL.MySQL2.Core;

/// <summary>
/// Marker methods recognized by the expression translator and rewritten into MySQL
/// function calls. Calls to these members are never executed at runtime in a query
/// context — the translator peels them out of the expression tree. Invoking them
/// directly outside a query throws, matching the pattern used by EF Core's
/// <c>EF.Functions</c>.
/// </summary>
public static class SqlFn
{
    // ── Date/time ─────────────────────────────────────────────────────────────

    /// <summary>MySQL <c>YEAR(d)</c>.</summary>
    public static int Year(DateTime d) => throw OutsideQuery(nameof(Year));
    /// <summary>MySQL <c>MONTH(d)</c>.</summary>
    public static int Month(DateTime d) => throw OutsideQuery(nameof(Month));
    /// <summary>MySQL <c>DAY(d)</c>.</summary>
    public static int Day(DateTime d) => throw OutsideQuery(nameof(Day));
    /// <summary>MySQL <c>HOUR(d)</c>.</summary>
    public static int Hour(DateTime d) => throw OutsideQuery(nameof(Hour));
    /// <summary>MySQL <c>MINUTE(d)</c>.</summary>
    public static int Minute(DateTime d) => throw OutsideQuery(nameof(Minute));

    /// <summary>
    /// Day of week, matching .NET's <c>DayOfWeek</c> numbering (0 = Sunday … 6 = Saturday).
    /// MySQL's <c>DAYOFWEEK</c> is 1 = Sunday, so the translator emits <c>DAYOFWEEK(d) - 1</c>.
    /// </summary>
    public static int DayOfWeek(DateTime d) => throw OutsideQuery(nameof(DayOfWeek));

    /// <summary>MySQL <c>DATE(d)</c> — strips the time component.</summary>
    public static DateTime Date(DateTime d) => throw OutsideQuery(nameof(Date));

    /// <summary>
    /// Rounds <paramref name="d"/> down to the nearest <paramref name="seconds"/>-wide
    /// bucket. Translates to
    /// <c>FROM_UNIXTIME(FLOOR(UNIX_TIMESTAMP(d)/seconds)*seconds)</c>. Use for time-
    /// series bucketing in <c>GroupBy</c> keys.
    /// </summary>
    public static DateTime BucketUtc(DateTime d, int seconds) => throw OutsideQuery(nameof(BucketUtc));

    // ── Conditional / null ────────────────────────────────────────────────────

    /// <summary>MySQL <c>COALESCE(a, b, ...)</c>.</summary>
    public static T Coalesce<T>(params T[] values) => throw OutsideQuery(nameof(Coalesce));
    /// <summary>MySQL <c>IFNULL(v, fallback)</c>.</summary>
    public static T IfNull<T>(T value, T fallback) => throw OutsideQuery(nameof(IfNull));

    // ── String ────────────────────────────────────────────────────────────────

    /// <summary>MySQL <c>LOWER(s)</c>.</summary>
    public static string Lower(string s) => throw OutsideQuery(nameof(Lower));
    /// <summary>MySQL <c>UPPER(s)</c>.</summary>
    public static string Upper(string s) => throw OutsideQuery(nameof(Upper));
    /// <summary>MySQL <c>CONCAT(a, b, …)</c>.</summary>
    public static string Concat(params string[] parts) => throw OutsideQuery(nameof(Concat));
    /// <summary>
    /// MySQL <c>s LIKE pattern</c>. Unlike the <c>Contains</c>/<c>StartsWith</c>/<c>EndsWith</c>
    /// visitor shortcuts, this passes <paramref name="pattern"/> through untouched — the
    /// caller supplies <c>%</c>/<c>_</c> wildcards directly.
    /// </summary>
    public static bool Like(string s, string pattern) => throw OutsideQuery(nameof(Like));

    // ── Math ──────────────────────────────────────────────────────────────────

    /// <summary>MySQL <c>ROUND(v, digits)</c>.</summary>
    public static double Round(double v, int digits) => throw OutsideQuery(nameof(Round));
    /// <summary>MySQL <c>FLOOR(v)</c>.</summary>
    public static double Floor(double v) => throw OutsideQuery(nameof(Floor));
    /// <summary>MySQL <c>CEILING(v)</c>.</summary>
    public static double Ceiling(double v) => throw OutsideQuery(nameof(Ceiling));

    [EditorBrowsable(EditorBrowsableState.Never)]
    private static InvalidOperationException OutsideQuery(string name) =>
        new($"SqlFn.{name} cannot be invoked outside a CL.MySQL2 query expression. " +
            "These methods are markers rewritten to SQL by the expression translator.");
}
