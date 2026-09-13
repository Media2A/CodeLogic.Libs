using CodeLogic.Core.Logging;

namespace CL.MySQL2.Core;

/// <summary>
/// Process-wide knobs consumed by the SQL/DDL generators, which are static and therefore
/// have no connection to hang per-database configuration off. <c>MySQL2Library</c> applies
/// them at initialization from the <c>Default</c> database (or, if there is no such entry,
/// the first enabled one). The defaults here match the shipped configuration defaults, so
/// an unconfigured process behaves exactly as it did before this type existed.
/// </summary>
internal static class SqlGenerationOptions
{
    /// <summary>
    /// Default VARCHAR length for an inferred string column, from
    /// <c>MySqlDatabaseConfig.DefaultStringSize</c>. Default 255.
    /// </summary>
    internal static int DefaultStringSize { get; private set; } = 255;

    /// <summary>
    /// Advisory ceiling for generated <c>IN (...)</c> lists, from
    /// <c>MySqlDatabaseConfig.MaxInClauseValues</c>. Exceeding it logs a warning; nothing is
    /// chunked or rejected. Default 1000.
    /// </summary>
    internal static int MaxInClauseValues { get; private set; } = 1_000;

    /// <summary>Logger used for the IN-clause warning. Null until the library initializes.</summary>
    internal static ILogger? Logger { get; private set; }

    /// <summary>Applies configuration. Called by <c>MySQL2Library.OnInitializeAsync</c>.</summary>
    internal static void Configure(int defaultStringSize, int maxInClauseValues, ILogger? logger)
    {
        if (defaultStringSize > 0) DefaultStringSize = defaultStringSize;
        if (maxInClauseValues > 0) MaxInClauseValues = maxInClauseValues;
        Logger = logger;
    }

    /// <summary>Restores the shipped defaults. Test hook.</summary>
    internal static void Reset()
    {
        DefaultStringSize = 255;
        MaxInClauseValues = 1_000;
    }
}
