using CodeLogic.Core.Events;
using CodeLogic.Core.Logging;

namespace CL.Storage.Registry;

internal sealed class StorageEventPublisher(IEventBus events, ILogger logger)
{
    public async Task PublishAsync<TEvent>(TEvent @event) where TEvent : IEvent
    {
        try
        {
            await events.PublishAsync(@event).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            try { logger.Error($"Storage event publication failed for '{typeof(TEvent).Name}'.", error); }
            catch { }
        }
    }
}
