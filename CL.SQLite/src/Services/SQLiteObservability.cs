using CL.SQLite.Events;
using CL.SQLite.Localization;
using CodeLogic.Core.Events;

namespace CL.SQLite.Services;

/// <summary>
/// Process-wide sink for the notifications the query pipelines raise, and the holder for the
/// library's localized strings. <see cref="Repository{T}"/>, <see cref="QueryBuilder{T}"/>,
/// <see cref="ConnectionManager"/> and <see cref="TableSyncService"/> are constructed without an
/// <see cref="IEventBus"/>, so they publish through this sink; <c>SQLiteLibrary</c> binds it at
/// initialization time. Until then every method is a no-op and <see cref="Strings"/> returns the
/// built-in English defaults.
/// </summary>
public static class SQLiteObservability
{
    private static IEventBus? _events;
    private static SQLiteStrings _strings = new();

    /// <summary>The library's localized strings; never null. Defaults to the built-in English set.</summary>
    public static SQLiteStrings Strings => _strings;

    /// <summary>Binds this sink to CodeLogic's event bus and localization. Called by <c>SQLiteLibrary</c>.</summary>
    /// <param name="events">The event bus published to, or <c>null</c> to publish nothing.</param>
    /// <param name="strings">The resolved localization model, or <c>null</c> to keep the defaults.</param>
    public static void Configure(IEventBus? events, SQLiteStrings? strings = null)
    {
        _events = events;
        if (strings is not null) _strings = strings;
    }

    /// <summary>Unbinds the sink. Called by <c>SQLiteLibrary</c> when the library stops.</summary>
    public static void Reset() => _events = null;

    /// <summary>
    /// Publishes <see cref="SlowQueryEvent"/> for a query that exceeded the connection's
    /// <c>SlowQueryThresholdMs</c>. The caller logs; this only raises the event.
    /// </summary>
    /// <param name="tableName">The table the query targeted.</param>
    /// <param name="sql">The SQL that ran.</param>
    /// <param name="elapsedMs">How long it took, in milliseconds.</param>
    public static void RecordSlowQuery(string tableName, string sql, long elapsedMs)
    {
        if (_events is null) return;
        _ = _events.PublishAsync(new SlowQueryEvent(tableName, sql, elapsedMs, DateTime.UtcNow));
    }
}
