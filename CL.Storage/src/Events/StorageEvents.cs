using CL.Storage.Models;
using CodeLogic.Core.Events;

namespace CL.Storage.Events;

public sealed record StorageItemWrittenEvent(
    string ConnectionId,
    StorageProvider Provider,
    string Path,
    DateTimeOffset Timestamp) : IEvent;

public sealed record StorageItemDeletedEvent(
    string ConnectionId,
    StorageProvider Provider,
    string Path,
    DateTimeOffset Timestamp) : IEvent;

public sealed record StorageItemCopiedEvent(
    string ConnectionId,
    StorageProvider Provider,
    string SourcePath,
    string DestinationPath,
    DateTimeOffset Timestamp) : IEvent;

public sealed record StorageItemMovedEvent(
    string ConnectionId,
    StorageProvider Provider,
    string SourcePath,
    string DestinationPath,
    DateTimeOffset Timestamp) : IEvent;
