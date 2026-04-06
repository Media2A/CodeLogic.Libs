using CodeLogic.Core.Logging;
using Npgsql;

namespace CL.PostgreSQL.Services;

/// <summary>
/// Wraps an NpgsqlTransaction with an async-disposable pattern.
/// When disposed without an explicit CommitAsync or RollbackAsync call,
/// the transaction is automatically rolled back.
/// </summary>
public sealed class TransactionScope : IAsyncDisposable
{
    private readonly ILogger? _logger;
    private bool _completed;

    internal string ConnectionId { get; }
    internal NpgsqlConnection Connection { get; }
    internal NpgsqlTransaction Transaction { get; }

    internal TransactionScope(
        string connectionId,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ILogger? logger = null)
    {
        ConnectionId = connectionId;
        Connection = connection;
        Transaction = transaction;
        _logger = logger;
    }

    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (_completed)
            throw new InvalidOperationException("Transaction has already been committed or rolled back.");

        await Transaction.CommitAsync(ct).ConfigureAwait(false);
        _completed = true;
        _logger?.Info($"[PostgreSQL] Transaction committed for '{ConnectionId}'");
    }

    public async Task RollbackAsync(CancellationToken ct = default)
    {
        if (_completed)
            throw new InvalidOperationException("Transaction has already been committed or rolled back.");

        await Transaction.RollbackAsync(ct).ConfigureAwait(false);
        _completed = true;
        _logger?.Info($"[PostgreSQL] Transaction rolled back for '{ConnectionId}'");
    }

    public async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            try
            {
                await Transaction.RollbackAsync().ConfigureAwait(false);
                _logger?.Warning($"[PostgreSQL] Transaction auto-rolled back on dispose for '{ConnectionId}'");
            }
            catch (Exception ex)
            {
                _logger?.Error($"[PostgreSQL] Error during auto-rollback for '{ConnectionId}': {ex.Message}", ex);
            }
            _completed = true;
        }

        await Transaction.DisposeAsync().ConfigureAwait(false);
        await Connection.CloseAsync().ConfigureAwait(false);
        await Connection.DisposeAsync().ConfigureAwait(false);
    }
}
