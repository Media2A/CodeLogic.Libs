using System.ComponentModel;

namespace CL.PostgreSQL.Core;

/// <summary>
/// Marker methods recognized by the expression translator and rewritten into PostgreSQL
/// function calls. Calls to these members are never executed at runtime in a query
/// context — the translator peels them out of the expression tree. Invoking them
/// directly outside a query throws, matching the pattern used by EF Core's
/// <c>EF.Functions</c>.
/// </summary>
public static class SqlFn
{
    // ── Date/time ─────────────────────────────────────────────────────────────

    /// <summary>PostgreSQL <c>EXTRACT(YEAR FROM d)::int</c>, evaluated in UTC.</summary>
    public static int Year(DateTime d) => throw OutsideQuery(nameof(Year));
    /// <summary>PostgreSQL <c>EXTRACT(MONTH FROM d)::int</c>, evaluated in UTC.</summary>
    public static int Month(DateTime d) => throw OutsideQuery(nameof(Month));
    /// <summary>PostgreSQL <c>EXTRACT(DAY FROM d)::int</c>, evaluated in UTC.</summary>
    public static int Day(DateTime d) => throw OutsideQuery(nameof(Day));
    /// <summary>PostgreSQL <c>EXTRACT(HOUR FROM d)::int</c>, evaluated in UTC.</summary>
    public static int Hour(DateTime d) => throw OutsideQuery(nameof(Hour));
    /// <summary>PostgreSQL <c>EXTRACT(MINUTE FROM d)::int</c>, evaluated in UTC.</summary>
    public static int Minute(DateTime d) => throw OutsideQuery(nameof(Minute));

    /// <summary>
    /// Day of week, matching .NET's <c>DayOfWeek</c> numbering (0 = Sunday … 6 = Saturday).
    /// PostgreSQL's <c>EXTRACT(DOW …)</c> already uses that numbering, so no adjustment is
    /// applied here — unlike the MySQL library, whose <c>DAYOFWEEK</c> is 1-based.
    /// </summary>
    public static int DayOfWeek(DateTime d) => throw OutsideQuery(nameof(DayOfWeek));

    /// <summary>
    /// PostgreSQL <c>(d AT TIME ZONE 'UTC')::date</c> — strips the time component, taking
    /// the date in UTC so it does not swing with the server's session time zone.
    /// </summary>
    public static DateTime Date(DateTime d) => throw OutsideQuery(nameof(Date));

    /// <summary>
    /// Rounds <paramref name="d"/> down to the nearest <paramref name="seconds"/>-wide
    /// bucket. Translates to
    /// <c>to_timestamp(floor(extract(epoch from d)/seconds)*seconds)</c>. Use for time-
    /// series bucketing in <c>GroupBy</c> keys.
    /// </summary>
    public static DateTime BucketUtc(DateTime d, int seconds) => throw OutsideQuery(nameof(BucketUtc));

    // ── Conditional / null ────────────────────────────────────────────────────

    /// <summary>PostgreSQL <c>COALESCE(a, b, ...)</c>.</summary>
    public static T Coalesce<T>(params T[] values) => throw OutsideQuery(nameof(Coalesce));
    /// <summary>Two-argument <c>COALESCE(v, fallback)</c> — PostgreSQL has no <c>IFNULL</c>.</summary>
    public static T IfNull<T>(T value, T fallback) => throw OutsideQuery(nameof(IfNull));

    // ── String ────────────────────────────────────────────────────────────────

    /// <summary>PostgreSQL <c>LOWER(s)</c>.</summary>
    public static string Lower(string s) => throw OutsideQuery(nameof(Lower));
    /// <summary>PostgreSQL <c>UPPER(s)</c>.</summary>
    public static string Upper(string s) => throw OutsideQuery(nameof(Upper));
    /// <summary>PostgreSQL <c>CONCAT(a, b, …)</c>.</summary>
    public static string Concat(params string[] parts) => throw OutsideQuery(nameof(Concat));
    /// <summary>
    /// PostgreSQL <c>s LIKE pattern</c>. Unlike the <c>Contains</c>/<c>StartsWith</c>/<c>EndsWith</c>
    /// visitor shortcuts, this passes <paramref name="pattern"/> through untouched — the
    /// caller supplies <c>%</c>/<c>_</c> wildcards directly.
    /// </summary>
    public static bool Like(string s, string pattern) => throw OutsideQuery(nameof(Like));

    // ── Math ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Rounds to <paramref name="digits"/> decimals. PostgreSQL has no
    /// <c>round(double precision, integer)</c> — only the numeric form takes a digit count —
    /// so this emits <c>ROUND(v::numeric, digits)::double precision</c>.
    /// </summary>
    public static double Round(double v, int digits) => throw OutsideQuery(nameof(Round));
    /// <summary>PostgreSQL <c>FLOOR(v)</c>.</summary>
    public static double Floor(double v) => throw OutsideQuery(nameof(Floor));
    /// <summary>PostgreSQL <c>CEILING(v)</c>.</summary>
    public static double Ceiling(double v) => throw OutsideQuery(nameof(Ceiling));

    [EditorBrowsable(EditorBrowsableState.Never)]
    private static InvalidOperationException OutsideQuery(string name) =>
        new($"SqlFn.{name} cannot be invoked outside a CL.PostgreSQL query expression. " +
            "These methods are markers rewritten to SQL by the expression translator.");
}
