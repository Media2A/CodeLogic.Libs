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

/// <summary>Published after a relayed copy between two storage connections completes.</summary>
public sealed record StorageCrossConnectionCopyCompletedEvent(
    string SourceConnectionId,
    StorageProvider SourceProvider,
    string SourcePath,
    string DestinationConnectionId,
    StorageProvider DestinationProvider,
    string DestinationPath,
    long Files,
    long Directories,
    long Bytes,
    DateTimeOffset Timestamp) : IEvent;

/// <summary>Published after a relayed move between two storage connections and source deletion complete.</summary>
public sealed record StorageCrossConnectionMoveCompletedEvent(
    string SourceConnectionId,
    StorageProvider SourceProvider,
    string SourcePath,
    string DestinationConnectionId,
    StorageProvider DestinationProvider,
    string DestinationPath,
    long Files,
    long Directories,
    long Bytes,
    DateTimeOffset Timestamp) : IEvent;

/// <summary>Published after a local directory has been uploaded into one storage connection.</summary>
public sealed record StorageDirectoryUploadedEvent(
    string DestinationConnectionId,
    StorageProvider DestinationProvider,
    string DestinationPath,
    long Files,
    long Directories,
    long Bytes,
    DateTimeOffset Timestamp) : IEvent;

/// <summary>Published after one storage directory has been downloaded to a caller-selected local directory.</summary>
public sealed record StorageDirectoryDownloadedEvent(
    string SourceConnectionId,
    StorageProvider SourceProvider,
    string SourcePath,
    long Files,
    long Directories,
    long Bytes,
    DateTimeOffset Timestamp) : IEvent;
