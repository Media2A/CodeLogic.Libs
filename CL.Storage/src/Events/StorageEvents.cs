using CL.Storage.Models;
using CodeLogic.Core.Events;

namespace CL.Storage.Events;

/// <summary>Published after file content has committed to one storage connection.</summary>
/// <param name="ConnectionId">Connection that accepted the content.</param>
/// <param name="Provider">Provider used by the connection.</param>
/// <param name="Path">Normalized destination path.</param>
/// <param name="Timestamp">UTC completion timestamp.</param>
public sealed record StorageItemWrittenEvent(
    string ConnectionId,
    StorageProvider Provider,
    string Path,
    DateTimeOffset Timestamp) : IEvent;

/// <summary>Published after a storage item has been deleted.</summary>
/// <param name="ConnectionId">Connection from which the item was deleted.</param>
/// <param name="Provider">Provider used by the connection.</param>
/// <param name="Path">Normalized deleted path.</param>
/// <param name="Timestamp">UTC completion timestamp.</param>
public sealed record StorageItemDeletedEvent(
    string ConnectionId,
    StorageProvider Provider,
    string Path,
    DateTimeOffset Timestamp) : IEvent;

/// <summary>Published after an item has been copied within one storage connection.</summary>
/// <param name="ConnectionId">Connection that performed the copy.</param>
/// <param name="Provider">Provider used by the connection.</param>
/// <param name="SourcePath">Normalized source path.</param>
/// <param name="DestinationPath">Normalized committed destination path.</param>
/// <param name="Timestamp">UTC completion timestamp.</param>
public sealed record StorageItemCopiedEvent(
    string ConnectionId,
    StorageProvider Provider,
    string SourcePath,
    string DestinationPath,
    DateTimeOffset Timestamp) : IEvent;

/// <summary>Published after an item has been moved within one storage connection.</summary>
/// <param name="ConnectionId">Connection that performed the move.</param>
/// <param name="Provider">Provider used by the connection.</param>
/// <param name="SourcePath">Normalized source path.</param>
/// <param name="DestinationPath">Normalized committed destination path.</param>
/// <param name="Timestamp">UTC completion timestamp.</param>
public sealed record StorageItemMovedEvent(
    string ConnectionId,
    StorageProvider Provider,
    string SourcePath,
    string DestinationPath,
    DateTimeOffset Timestamp) : IEvent;

/// <summary>Published after a relayed copy between two storage connections completes.</summary>
/// <param name="SourceConnectionId">Connection from which content was read.</param>
/// <param name="SourceProvider">Source provider kind.</param>
/// <param name="SourcePath">Normalized source path.</param>
/// <param name="DestinationConnectionId">Connection to which content was committed.</param>
/// <param name="DestinationProvider">Destination provider kind.</param>
/// <param name="DestinationPath">Normalized destination path.</param>
/// <param name="Files">Number of copied files.</param>
/// <param name="Directories">Number of copied directories.</param>
/// <param name="Bytes">Total copied content bytes.</param>
/// <param name="Timestamp">UTC completion timestamp.</param>
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
/// <param name="SourceConnectionId">Connection from which content was moved.</param>
/// <param name="SourceProvider">Source provider kind.</param>
/// <param name="SourcePath">Normalized deleted source path.</param>
/// <param name="DestinationConnectionId">Connection to which content was committed.</param>
/// <param name="DestinationProvider">Destination provider kind.</param>
/// <param name="DestinationPath">Normalized destination path.</param>
/// <param name="Files">Number of moved files.</param>
/// <param name="Directories">Number of moved directories.</param>
/// <param name="Bytes">Total moved content bytes.</param>
/// <param name="Timestamp">UTC completion timestamp.</param>
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
/// <param name="DestinationConnectionId">Connection that received the directory.</param>
/// <param name="DestinationProvider">Destination provider kind.</param>
/// <param name="DestinationPath">Normalized destination directory path.</param>
/// <param name="Files">Number of uploaded files.</param>
/// <param name="Directories">Number of uploaded directories.</param>
/// <param name="Bytes">Total uploaded content bytes.</param>
/// <param name="Timestamp">UTC completion timestamp.</param>
public sealed record StorageDirectoryUploadedEvent(
    string DestinationConnectionId,
    StorageProvider DestinationProvider,
    string DestinationPath,
    long Files,
    long Directories,
    long Bytes,
    DateTimeOffset Timestamp) : IEvent;

/// <summary>Published after one storage directory has been downloaded to a caller-selected local directory.</summary>
/// <param name="SourceConnectionId">Connection from which the directory was read.</param>
/// <param name="SourceProvider">Source provider kind.</param>
/// <param name="SourcePath">Normalized source directory path.</param>
/// <param name="Files">Number of downloaded files.</param>
/// <param name="Directories">Number of downloaded directories.</param>
/// <param name="Bytes">Total downloaded content bytes.</param>
/// <param name="Timestamp">UTC completion timestamp.</param>
public sealed record StorageDirectoryDownloadedEvent(
    string SourceConnectionId,
    StorageProvider SourceProvider,
    string SourcePath,
    long Files,
    long Directories,
    long Bytes,
    DateTimeOffset Timestamp) : IEvent;
