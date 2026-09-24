using CL.Storage.Models;
using CL.Storage.Queue;
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

/// <summary>Published when a session-oriented connection (FTP, SFTP) opens and authenticates a new session.</summary>
/// <param name="ConnectionId">Connection that opened the session.</param>
/// <param name="Provider">Provider used by the connection.</param>
/// <param name="Timestamp">UTC time the session became usable.</param>
public sealed record StorageConnectionOpenedEvent(
    string ConnectionId,
    StorageProvider Provider,
    DateTimeOffset Timestamp) : IEvent;

/// <summary>Published when a session dropped, timed out, or failed TLS and was retired instead of reused.</summary>
/// <param name="ConnectionId">Connection whose session was retired.</param>
/// <param name="Provider">Provider used by the connection.</param>
/// <param name="Operation">Operation that observed the failure.</param>
/// <param name="ErrorCode">Stable <c>storage.*</c> error code describing the failure.</param>
/// <param name="Timestamp">UTC time the failure was observed.</param>
public sealed record StorageConnectionLostEvent(
    string ConnectionId,
    StorageProvider Provider,
    string Operation,
    string ErrorCode,
    DateTimeOffset Timestamp) : IEvent;

/// <summary>Published before an operation is retried after a transient failure.</summary>
/// <param name="ConnectionId">Connection running the operation.</param>
/// <param name="Provider">Provider used by the connection.</param>
/// <param name="Operation">Operation being retried.</param>
/// <param name="Attempt">One-based retry number.</param>
/// <param name="Delay">Backoff before the retry starts.</param>
/// <param name="ErrorCode">Stable <c>storage.*</c> code of the failure that triggered the retry.</param>
/// <param name="Timestamp">UTC time the retry was scheduled.</param>
public sealed record StorageConnectionRetryEvent(
    string ConnectionId,
    StorageProvider Provider,
    string Operation,
    int Attempt,
    TimeSpan Delay,
    string ErrorCode,
    DateTimeOffset Timestamp) : IEvent;

/// <summary>Published when a queued transfer job starts an attempt.</summary>
/// <param name="JobId">Queue job identifier.</param>
/// <param name="Kind">What the job does.</param>
/// <param name="Source">Source description.</param>
/// <param name="Destination">Destination description.</param>
/// <param name="Timestamp">UTC start time.</param>
public sealed record StorageTransferStartedEvent(
    string JobId,
    StorageTransferKind Kind,
    string Source,
    string Destination,
    DateTimeOffset Timestamp) : IEvent;

/// <summary>Published when a queued transfer job completes.</summary>
/// <param name="JobId">Queue job identifier.</param>
/// <param name="Kind">What the job did.</param>
/// <param name="Source">Source description.</param>
/// <param name="Destination">Destination description.</param>
/// <param name="Attempts">Attempts it took, including automatic retries.</param>
/// <param name="Timestamp">UTC completion time.</param>
public sealed record StorageTransferCompletedEvent(
    string JobId,
    StorageTransferKind Kind,
    string Source,
    string Destination,
    int Attempts,
    DateTimeOffset Timestamp) : IEvent;

/// <summary>Published when a queued transfer job fails for good (after any automatic retries).</summary>
/// <param name="JobId">Queue job identifier.</param>
/// <param name="Kind">What the job did.</param>
/// <param name="Source">Source description.</param>
/// <param name="Destination">Destination description.</param>
/// <param name="Attempts">Attempts made.</param>
/// <param name="ErrorCode">Stable <c>storage.*</c> code of the last failure.</param>
/// <param name="Timestamp">UTC failure time.</param>
public sealed record StorageTransferFailedEvent(
    string JobId,
    StorageTransferKind Kind,
    string Source,
    string Destination,
    int Attempts,
    string ErrorCode,
    DateTimeOffset Timestamp) : IEvent;

/// <summary>Published when a health check finds a connection in a different state than the previous check did.</summary>
/// <param name="ConnectionId">Connection that was checked.</param>
/// <param name="Provider">Provider used by the connection.</param>
/// <param name="PreviouslyHealthy">Result of the previous check, or <see langword="null"/> for the first check.</param>
/// <param name="Healthy">Result of this check.</param>
/// <param name="ErrorCode">Stable <c>storage.*</c> code when the check failed.</param>
/// <param name="Latency">How long the check took.</param>
/// <param name="Timestamp">UTC time the check finished.</param>
public sealed record StorageConnectionHealthChangedEvent(
    string ConnectionId,
    StorageProvider Provider,
    bool? PreviouslyHealthy,
    bool Healthy,
    string? ErrorCode,
    TimeSpan Latency,
    DateTimeOffset Timestamp) : IEvent;

/// <summary>
/// Published when an operation on a connection's storage service returns a failure, including expected
/// ones such as <c>storage.not_found</c>; filter on <paramref name="ErrorCode"/> as needed. Input that is
/// rejected before reaching the provider (invalid paths or options) is not reported.
/// </summary>
/// <param name="ConnectionId">Connection that ran the operation.</param>
/// <param name="Provider">Provider used by the connection.</param>
/// <param name="Operation">Service method, such as <c>Upload</c> or <c>GetInfo</c>.</param>
/// <param name="Path">Normalized path the operation targeted, when it has one.</param>
/// <param name="ErrorCode">Stable <c>storage.*</c> error code.</param>
/// <param name="Timestamp">UTC time the failure was returned.</param>
public sealed record StorageOperationFailedEvent(
    string ConnectionId,
    StorageProvider Provider,
    string Operation,
    string? Path,
    string ErrorCode,
    DateTimeOffset Timestamp) : IEvent;

/// <summary>Published when a queued transfer job is cancelled.</summary>
/// <param name="JobId">Queue job identifier.</param>
/// <param name="Kind">What the job did.</param>
/// <param name="Source">Source description.</param>
/// <param name="Destination">Destination description.</param>
/// <param name="Timestamp">UTC time.</param>
public sealed record StorageTransferCancelledEvent(
    string JobId,
    StorageTransferKind Kind,
    string Source,
    string Destination,
    DateTimeOffset Timestamp) : IEvent;

/// <summary>Published when a queued transfer job failed transiently and will be retried.</summary>
/// <param name="JobId">Queue job identifier.</param>
/// <param name="Kind">What the job does.</param>
/// <param name="Source">Source description.</param>
/// <param name="Destination">Destination description.</param>
/// <param name="Attempts">Attempts made so far.</param>
/// <param name="Delay">Backoff before the next attempt.</param>
/// <param name="ErrorCode">Stable <c>storage.*</c> code of the failure.</param>
/// <param name="Timestamp">UTC time.</param>
public sealed record StorageTransferRetryingEvent(
    string JobId,
    StorageTransferKind Kind,
    string Source,
    string Destination,
    int Attempts,
    TimeSpan Delay,
    string ErrorCode,
    DateTimeOffset Timestamp) : IEvent;

/// <summary>Published when a queued transfer job is blocked on trust or credentials.</summary>
/// <param name="JobId">Queue job identifier.</param>
/// <param name="Kind">What the job does.</param>
/// <param name="Source">Source description.</param>
/// <param name="Destination">Destination description.</param>
/// <param name="Reason">What a person needs to fix.</param>
/// <param name="ErrorCode">Stable <c>storage.*</c> code of the failure.</param>
/// <param name="Timestamp">UTC time.</param>
public sealed record StorageTransferBlockedEvent(
    string JobId,
    StorageTransferKind Kind,
    string Source,
    string Destination,
    StorageTransferBlockReason Reason,
    string ErrorCode,
    DateTimeOffset Timestamp) : IEvent;

/// <summary>Published when a queued transfer job stopped with a mixed state that needs reconciling.</summary>
/// <param name="JobId">Queue job identifier.</param>
/// <param name="Kind">What the job did.</param>
/// <param name="Source">Source description.</param>
/// <param name="Destination">Destination description.</param>
/// <param name="ErrorCode">Stable <c>storage.*</c> code of the failure.</param>
/// <param name="Timestamp">UTC time.</param>
public sealed record StorageTransferNeedsReconciliationEvent(
    string JobId,
    StorageTransferKind Kind,
    string Source,
    string Destination,
    string ErrorCode,
    DateTimeOffset Timestamp) : IEvent;

/// <summary>
/// Published when a queued transfer job becomes <see cref="StorageTransferState.Interrupted"/>: it was running
/// when its queue or process stopped, and it may have changed its destination (or requeueing is turned off).
/// </summary>
/// <param name="JobId">Queue job identifier.</param>
/// <param name="Kind">What the job does.</param>
/// <param name="Source">Source description.</param>
/// <param name="Destination">Destination description.</param>
/// <param name="Phase">How far the interrupted attempt got.</param>
/// <param name="Timestamp">UTC time.</param>
public sealed record StorageTransferInterruptedEvent(
    string JobId,
    StorageTransferKind Kind,
    string Source,
    string Destination,
    StorageTransferPhase Phase,
    DateTimeOffset Timestamp) : IEvent;
