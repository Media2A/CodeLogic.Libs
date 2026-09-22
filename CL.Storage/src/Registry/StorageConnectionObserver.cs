using CL.Storage.Events;
using CL.Storage.Models;
using CL.Storage.Providers;
using CodeLogic.Core.Results;

namespace CL.Storage.Registry;

/// <summary>Turns backend connection notifications into library events and log lines.</summary>
/// <remarks>
/// Backends are created before the library context is always available and outlive configuration
/// reloads, so the publisher is resolved per notification. Notifications are fire-and-forget: a slow or
/// failing event handler must never delay or fail the storage operation that raised it.
/// </remarks>
internal sealed class StorageConnectionObserver(Func<StorageEventPublisher?> publisher, Func<CodeLogic.Core.Logging.ILogger?> logger)
    : IStorageConnectionObserver
{
    public void SessionOpened(string connectionId, StorageProvider provider) =>
        Publish(new StorageConnectionOpenedEvent(connectionId, provider, DateTimeOffset.UtcNow));

    public void SessionFaulted(string connectionId, StorageProvider provider, string operation, Error error)
    {
        Log($"[Storage] {provider} connection '{connectionId}' lost its session during '{operation}' ({error.Code}); the session was retired.");
        Publish(new StorageConnectionLostEvent(connectionId, provider, operation, error.Code, DateTimeOffset.UtcNow));
    }

    public void Retrying(string connectionId, StorageProvider provider, string operation, int attempt, TimeSpan delay, Error error)
    {
        Log($"[Storage] Transient error on {provider} connection '{connectionId}' during '{operation}' ({error.Code}); retry {attempt} in {delay.TotalMilliseconds:0} ms.");
        Publish(new StorageConnectionRetryEvent(connectionId, provider, operation, attempt, delay, error.Code, DateTimeOffset.UtcNow));
    }

    private void Publish<TEvent>(TEvent @event) where TEvent : CodeLogic.Core.Events.IEvent
    {
        try
        {
            if (publisher() is { } target)
                _ = target.PublishAsync(@event);
        }
        catch { /* The library may be stopping; connection events are best-effort. */ }
    }

    private void Log(string message)
    {
        try { logger()?.Warning(message); }
        catch { /* Logging must not affect storage operations. */ }
    }
}
