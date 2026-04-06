using CodeLogic.Core.Logging;
using MySqlConnector;

namespace CL.MySQL2.Services;

/// <summary>
/// Wraps a <see cref="MySqlTransaction"/> with an async-disposable pattern.
/// When disposed without an explicit <see cref="CommitAsync"/> or <see cref="RollbackAsync"/> call,
/// the transaction is automatically rolled back.
/// </summary>
public sealed class TransactionScope : IAsyncDisposable
{
    private readonly ILogger? _logger;
    private bool _completed;

    /// <summary>The connection ID this transaction belongs to.</summary>
    internal string ConnectionId { get; }

    /// <summary>The underlying database connection.</summary>
    internal MySqlConnection Connection { get; }

    /// <summary>The underlying database transaction.</summary>
    internal MySqlTransaction Transaction { get; }

    internal TransactionScope(
        string connectionId,
        MySqlConnection connection,
        MySqlTransaction transaction,
        ILogger? logger = null)
    {
        ConnectionId = connectionId;
        Connection = connection;
        Transaction = transaction;
        _logger = logger;
    }

    /// <summary>
    /// Commits the transaction.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if already committed or rolled back.</exception>
    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (_completed)
            throw new InvalidOperationException("Transaction has already been committed or rolled back.");

        await Transaction.CommitAsync(ct).ConfigureAwait(false);
        _completed = true;
        _logger?.Info($"[MySQL2] Transaction committed for '{ConnectionId}'");
    }

    /// <summary>
    /// Rolls back the transaction explicitly.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if already committed or rolled back.</exception>
    public async Task RollbackAsync(CancellationToken ct = default)
    {
        if (_completed)
            throw new InvalidOperationException("Transaction has already been committed or rolled back.");

        await Transaction.RollbackAsync(ct).ConfigureAwait(false);
        _completed = true;
        _logger?.Info($"[MySQL2] Transaction rolled back for '{ConnectionId}'");
    }

    /// <summary>
    /// Disposes the transaction. Automatically rolls back if not already committed/rolled back.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            try
            {
                await Transaction.RollbackAsync().ConfigureAwait(false);
                _logger?.Warning($"[MySQL2] Transaction auto-rolled back on dispose for '{ConnectionId}'");
            }
            catch (Exception ex)
            {
                _logger?.Error($"[MySQL2] Error during auto-rollback for '{ConnectionId}': {ex.Message}", ex);
            }
            _completed = true;
        }

        await Transaction.DisposeAsync().ConfigureAwait(false);
        await Connection.CloseAsync().ConfigureAwait(false);
        await Connection.DisposeAsync().ConfigureAwait(false);
    }
}
