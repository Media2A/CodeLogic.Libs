using System.Text.Json;
using System.Text.Json.Serialization;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Queue;

/// <summary>What a queued job does.</summary>
public enum StorageTransferKind
{
    /// <summary>Copies an item between (or within) connections.</summary>
    Copy = 0,
    /// <summary>Moves an item between (or within) connections.</summary>
    Move = 1,
    /// <summary>Uploads a local file.</summary>
    UploadFile = 2,
    /// <summary>Downloads to a local file.</summary>
    DownloadFile = 3,
    /// <summary>Uploads a local directory tree.</summary>
    UploadDirectory = 4,
    /// <summary>Downloads a directory tree to a local directory.</summary>
    DownloadDirectory = 5
}

/// <summary>
/// Where a job is in its life cycle. The numbers of the first five states are the ones 4.8.93 stored; states
/// added since are appended, so a stored number keeps its meaning.
/// </summary>
public enum StorageTransferState
{
    /// <summary>Waiting for a free slot, or for its retry time.</summary>
    Queued = 0,
    /// <summary>Transferring.</summary>
    Running = 1,
    /// <summary>Finished successfully (or skipped by its conflict policy).</summary>
    Completed = 2,
    /// <summary>Failed permanently or ran out of retries; can be retried.</summary>
    Failed = 3,
    /// <summary>Cancelled by the caller.</summary>
    Cancelled = 4,
    /// <summary>Held by <see cref="StorageTransferQueue.PauseJobAsync"/>; a resumable transfer continues where it stopped.</summary>
    Paused = 5,
    /// <summary>Stopped by something only a person can fix: an untrusted server identity or refused credentials. See <see cref="StorageTransferJob.BlockReason"/>.</summary>
    Blocked = 6,
    /// <summary>Stopped part-way with a mixed state (see the job's report); decide, then retry or remove it.</summary>
    NeedsReconciliation = 7,
    /// <summary>
    /// Was running when its process stopped, and either may have changed the destination or
    /// <see cref="StorageTransferQueueOptions.RequeueInterruptedWhenSafe"/> is off; decide, then retry or remove it.
    /// </summary>
    Interrupted = 8
}

/// <summary>Why a job is <see cref="StorageTransferState.Blocked"/>.</summary>
public enum StorageTransferBlockReason
{
    /// <summary>The server's certificate or host key is not trusted.</summary>
    Trust = 0,
    /// <summary>The credentials, or the client certificate, were refused.</summary>
    Credential = 1
}

/// <summary>How far a running job got, recorded so a restart knows what may have changed.</summary>
public enum StorageTransferPhase
{
    /// <summary>Nothing written yet.</summary>
    NotStarted = 0,
    /// <summary>Writing to a staging object; the destination is untouched.</summary>
    Transferring = 1,
    /// <summary>Changing the destination.</summary>
    Committing = 2,
    /// <summary>The destination is complete; deleting the source of a move.</summary>
    DeletingSource = 3
}

/// <summary>A job's durable progress marker.</summary>
/// <param name="Phase">How far the last attempt got.</param>
/// <param name="ResumeToken">Staged data the next attempt can continue.</param>
public sealed record StorageTransferCheckpoint(StorageTransferPhase Phase, StorageResumeToken? ResumeToken = null);

/// <summary>
/// A job described entirely as data — what to transfer, between which endpoints, with which options — so it
/// can be stored and rebuilt after a restart. Progress sinks are not part of a spec; the queue supplies them.
/// </summary>
public sealed record StorageTransferJobSpec
{
    /// <summary>The spec format this version of the library writes.</summary>
    public const int CurrentSchemaVersion = 1;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Gets the format the spec was written in. Enums are stored by name. A spec from a newer format fails
    /// to read instead of being misread.
    /// </summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Gets what the job does.</summary>
    public required StorageTransferKind Kind { get; init; }
    /// <summary>Gets the source connection, for copies, moves, and downloads.</summary>
    public string? SourceConnectionId { get; init; }
    /// <summary>Gets the source path, or the local file or directory for uploads.</summary>
    public required string SourcePath { get; init; }
    /// <summary>Gets the destination connection, for copies, moves, and uploads.</summary>
    public string? DestinationConnectionId { get; init; }
    /// <summary>Gets the destination path, or the local file or directory for downloads.</summary>
    public required string DestinationPath { get; init; }
    /// <summary>Gets options for copies, moves, and directory transfers.</summary>
    public StorageTransferOptions? TransferOptions { get; init; }
    /// <summary>Gets options for file uploads.</summary>
    public StorageUploadOptions? UploadOptions { get; init; }
    /// <summary>Gets options for file downloads (version, range).</summary>
    public StorageDownloadOptions? DownloadOptions { get; init; }
    /// <summary>Gets the conflict policy for the local file of a download.</summary>
    public StorageConflictPolicy? DownloadConflictPolicy { get; init; }

    /// <summary>Gets a short description of the source, <c>connection:path</c> or a local path.</summary>
    [JsonIgnore]
    public string SourceLabel => SourceConnectionId is null ? SourcePath : $"{SourceConnectionId}:{SourcePath}";
    /// <summary>Gets a short description of the destination, <c>connection:path</c> or a local path.</summary>
    [JsonIgnore]
    public string DestinationLabel => DestinationConnectionId is null ? DestinationPath : $"{DestinationConnectionId}:{DestinationPath}";

    /// <summary>Gets the connections the job uses, for per-connection limits.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Connections =>
        [.. new[] { SourceConnectionId, DestinationConnectionId }.Where(id => id is not null).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Serializes the spec, for stores that keep jobs as JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Reads a spec written by <see cref="ToJson"/>.</summary>
    /// <exception cref="JsonException">The JSON is empty or was written by a newer schema version.</exception>
    public static StorageTransferJobSpec FromJson(string json)
    {
        var spec = JsonSerializer.Deserialize<StorageTransferJobSpec>(json, Json) ?? throw new JsonException("The job spec is empty.");
        if (spec.SchemaVersion > CurrentSchemaVersion)
            throw new JsonException($"The job spec has schema version {spec.SchemaVersion}; this library reads up to {CurrentSchemaVersion}.");
        return spec;
    }

    /// <summary>
    /// Whether two specs describe the same work. Connection ids compare without regard to case, as connections
    /// do, and options left null equal default options of the kind the job uses.
    /// </summary>
    public bool SameWorkAs(StorageTransferJobSpec other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Normalized().ToJson() == other.Normalized().ToJson();
    }

    private StorageTransferJobSpec Normalized() => this with
    {
        SourceConnectionId = SourceConnectionId?.ToUpperInvariant(),
        DestinationConnectionId = DestinationConnectionId?.ToUpperInvariant(),
        TransferOptions = UsesTransferOptions ? TransferOptions ?? new StorageTransferOptions() : TransferOptions,
        UploadOptions = Kind == StorageTransferKind.UploadFile ? UploadOptions ?? new StorageUploadOptions() : UploadOptions,
        DownloadOptions = Kind == StorageTransferKind.DownloadFile ? DownloadOptions ?? new StorageDownloadOptions() : DownloadOptions
    };

    private bool UsesTransferOptions => Kind is not (StorageTransferKind.UploadFile or StorageTransferKind.DownloadFile);

    internal Result Validate()
    {
        if (string.IsNullOrWhiteSpace(SourcePath) || string.IsNullOrWhiteSpace(DestinationPath))
            return Result.Failure(StorageErrors.InvalidContent("A job needs a source and a destination."));
        var needsSource = Kind is StorageTransferKind.Copy or StorageTransferKind.Move or StorageTransferKind.DownloadFile or StorageTransferKind.DownloadDirectory;
        var needsDestination = Kind is StorageTransferKind.Copy or StorageTransferKind.Move or StorageTransferKind.UploadFile or StorageTransferKind.UploadDirectory;
        if (needsSource && string.IsNullOrWhiteSpace(SourceConnectionId))
            return Result.Failure(StorageErrors.InvalidContent($"A {Kind} job needs a source connection."));
        if (needsDestination && string.IsNullOrWhiteSpace(DestinationConnectionId))
            return Result.Failure(StorageErrors.InvalidContent($"A {Kind} job needs a destination connection."));
        // What does not apply to the kind would be ignored when the job runs, so it is refused here.
        if (!needsSource && SourceConnectionId is not null)
            return Result.Failure(StorageErrors.InvalidContent($"A {Kind} job reads a local source and takes no source connection."));
        if (!needsDestination && DestinationConnectionId is not null)
            return Result.Failure(StorageErrors.InvalidContent($"A {Kind} job writes a local destination and takes no destination connection."));
        if (!UsesTransferOptions && TransferOptions is not null)
            return Result.Failure(StorageErrors.InvalidContent($"A {Kind} job does not take TransferOptions."));
        if (Kind != StorageTransferKind.UploadFile && UploadOptions is not null)
            return Result.Failure(StorageErrors.InvalidContent($"A {Kind} job does not take UploadOptions."));
        if (Kind != StorageTransferKind.DownloadFile && (DownloadOptions is not null || DownloadConflictPolicy is not null))
            return Result.Failure(StorageErrors.InvalidContent($"A {Kind} job does not take DownloadOptions or DownloadConflictPolicy."));
        if (TransferOptions?.Progress is not null || UploadOptions?.Progress is not null || DownloadOptions?.Progress is not null)
            return Result.Failure(StorageErrors.InvalidContent("A job spec cannot carry a progress sink; subscribe to the queue's ProgressChanged instead."));
        return Result.Success();
    }
}

/// <summary>A failure recorded on a job, in a form a store can keep.</summary>
/// <param name="Code">The <c>storage.*</c> code.</param>
/// <param name="Message">The message.</param>
/// <param name="Details">Provider-neutral details.</param>
public sealed record StorageTransferFailure(string Code, string Message, string? Details)
{
    internal static StorageTransferFailure? From(Error? error) => error is null ? null : new(error.Code, error.Message, error.Details);

    internal Error ToError() => StorageErrors.Create(Code, Message, Details ?? string.Empty);
}

/// <summary>
/// A job as a store keeps it. Everything a queue needs to rebuild the job after a restart is here; progress
/// and the full report are not persisted.
/// </summary>
public sealed record StorageTransferJobRecord
{
    /// <summary>The record format this version of the library writes.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets the job's identifier, chosen by the caller or generated.</summary>
    public required string Id { get; init; }
    /// <summary>Gets the record format; a store that serializes records should keep it.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    /// <summary>
    /// Gets the store's revision of this record: 1 when added, one more with every save. A save carrying an
    /// older revision is refused, so a stale copy never overwrites a newer state.
    /// </summary>
    public long Revision { get; init; }
    /// <summary>Gets the work.</summary>
    public required StorageTransferJobSpec Spec { get; init; }
    /// <summary>Gets the priority; higher starts first.</summary>
    public int Priority { get; init; }
    /// <summary>Gets the position among jobs of the same priority; lower starts first.</summary>
    public long Order { get; init; }
    /// <summary>Gets the state.</summary>
    public StorageTransferState State { get; init; }
    /// <summary>Gets why a blocked job is blocked.</summary>
    public StorageTransferBlockReason? BlockReason { get; init; }
    /// <summary>Gets the attempts since the job was added or last retried by hand.</summary>
    public int Attempts { get; init; }
    /// <summary>Gets the automatic retries still available.</summary>
    public int RetriesLeft { get; init; }
    /// <summary>Gets when a queued job may start next.</summary>
    public DateTimeOffset? NextAttemptAt { get; init; }
    /// <summary>Gets the last failure.</summary>
    public StorageTransferFailure? Failure { get; init; }
    /// <summary>Gets how far the last attempt got, and what it staged.</summary>
    public StorageTransferCheckpoint Checkpoint { get; init; } = new(StorageTransferPhase.NotStarted);
    /// <summary>Gets when the job was added.</summary>
    public DateTimeOffset EnqueuedAt { get; init; }
    /// <summary>Gets when the last attempt started.</summary>
    public DateTimeOffset? StartedAt { get; init; }
    /// <summary>Gets when the job finished.</summary>
    public DateTimeOffset? FinishedAt { get; init; }
    /// <summary>Gets the worker holding the job's lease, while it runs. Owned by the store.</summary>
    public string? LeaseOwner { get; init; }
    /// <summary>Gets when the lease expires. Owned by the store.</summary>
    public DateTimeOffset? LeaseExpiresAt { get; init; }
    /// <summary>Gets the fencing token of the latest lease; it grows with every claim. Owned by the store.</summary>
    public long FencingToken { get; init; }

    /// <summary>Gets whether the job is finished and will not run again unless retried.</summary>
    [JsonIgnore]
    public bool IsFinished => State is StorageTransferState.Completed or StorageTransferState.Failed or StorageTransferState.Cancelled;

    /// <summary>
    /// Gets whether this version of the library can run the record: neither the record nor its spec was
    /// written by a newer schema. A queue leaves records it cannot read alone, for the version that wrote them.
    /// </summary>
    [JsonIgnore]
    public bool IsReadable => SchemaVersion <= CurrentSchemaVersion && Spec.SchemaVersion <= StorageTransferJobSpec.CurrentSchemaVersion;

    /// <summary>Serializes the record, lease fields and checkpoint included, for stores that keep jobs as JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, StorageTransferJobSpec.Json);

    /// <summary>Reads a record written by <see cref="ToJson"/>.</summary>
    /// <exception cref="JsonException">The JSON is empty or was written by a newer schema version.</exception>
    public static StorageTransferJobRecord FromJson(string json)
    {
        var record = JsonSerializer.Deserialize<StorageTransferJobRecord>(json, StorageTransferJobSpec.Json) ?? throw new JsonException("The job record is empty.");
        if (!record.IsReadable)
            throw new JsonException($"The job record has schema version {record.SchemaVersion} (spec {record.Spec.SchemaVersion}); this library reads up to {CurrentSchemaVersion} ({StorageTransferJobSpec.CurrentSchemaVersion}).");
        return record;
    }
}

/// <summary>A worker's claim on a job. Saves carrying a lease that is no longer current are refused.</summary>
/// <param name="JobId">The claimed job.</param>
/// <param name="WorkerId">The worker.</param>
/// <param name="FencingToken">Grows with every claim; an older token is stale.</param>
/// <param name="ExpiresAt">When the claim lapses unless renewed.</param>
public sealed record StorageTransferLease(string JobId, string WorkerId, long FencingToken, DateTimeOffset ExpiresAt);

/// <summary>A snapshot of one job, with its live progress and last report.</summary>
public sealed record StorageTransferJob
{
    /// <summary>Gets the stored form of the job.</summary>
    public required StorageTransferJobRecord Record { get; init; }
    /// <summary>Gets the latest progress of a running job.</summary>
    public StorageTransferProgress? Progress { get; init; }
    /// <summary>Gets the report of the last copy or move attempt, in this process.</summary>
    public StorageTransferReport? LastReport { get; init; }

    /// <summary>Gets the job's identifier.</summary>
    public string Id => Record.Id;
    /// <summary>Gets what the job does.</summary>
    public StorageTransferKind Kind => Record.Spec.Kind;
    /// <summary>Gets the state.</summary>
    public StorageTransferState State => Record.State;
    /// <summary>Gets why a blocked job is blocked.</summary>
    public StorageTransferBlockReason? BlockReason => Record.BlockReason;
    /// <summary>Gets the priority; higher starts first.</summary>
    public int Priority => Record.Priority;
    /// <summary>Gets the attempts so far.</summary>
    public int Attempts => Record.Attempts;
    /// <summary>Gets a description of the source.</summary>
    public string Source => Record.Spec.SourceLabel;
    /// <summary>Gets a description of the destination.</summary>
    public string Destination => Record.Spec.DestinationLabel;
    /// <summary>Gets the last failure.</summary>
    public Error? Error => Record.Failure?.ToError();
}
