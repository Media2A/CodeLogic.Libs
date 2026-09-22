namespace CL.Storage.Configuration;

/// <summary>Controls automatic retries of transient failures for one connection.</summary>
/// <remarks>
/// Only failures classified as transient by <see cref="Errors.StorageErrorInfo.IsTransient"/> are retried:
/// timeouts, refused or dropped connections, and busy servers. Reads, listings, and info calls are always
/// eligible. Uploads are retried only from a seekable source because the content must be replayed.
/// Deletes and moves are retried only when <see cref="RetryNonIdempotent"/> is enabled, since the first
/// attempt may have completed on the server before its reply was lost.
/// </remarks>
public sealed class StorageRetryConfig
{
    /// <summary>Gets or sets how many times a failed operation is repeated; zero disables retries.</summary>
    public int RetryCount { get; set; } = 3;

    /// <summary>Gets or sets the delay before the first retry in milliseconds; later retries double it.</summary>
    public int BaseDelayMs { get; set; } = 100;

    /// <summary>Gets or sets the upper bound for any single retry delay in milliseconds.</summary>
    public int MaxDelayMs { get; set; } = 30_000;

    /// <summary>Gets or sets whether deletes and moves are retried after a transient failure.</summary>
    public bool RetryNonIdempotent { get; set; }

    internal IEnumerable<string> GetValidationErrors(string prefix)
    {
        if (RetryCount is < 0 or > 10)
            yield return $"{prefix}RetryCount must be between 0 and 10";
        if (BaseDelayMs is < 1 or > 60_000)
            yield return $"{prefix}BaseDelayMs must be between 1 and 60000";
        if (MaxDelayMs < BaseDelayMs || MaxDelayMs > 300_000)
            yield return $"{prefix}MaxDelayMs must be between BaseDelayMs and 300000";
    }
}

/// <summary>Controls pooled sessions for session-oriented providers (FTP, SFTP).</summary>
public sealed class StorageSessionConfig
{
    /// <summary>Gets or sets the maximum number of open sessions, in use or idle, for this connection.</summary>
    /// <remarks>Keep this below the server's per-user connection limit; callers beyond it wait for a free session.</remarks>
    public int MaxSessions { get; set; } = 8;

    /// <summary>Gets or sets the maximum number of connected sessions kept idle for reuse.</summary>
    public int MaxIdleSessions { get; set; } = 4;

    /// <summary>Gets or sets how long an idle session is kept before it is closed, in seconds.</summary>
    public int IdleLifetimeSeconds { get; set; } = 120;

    /// <summary>Gets or sets how long a caller waits for a free session before failing with <c>storage.server_busy</c>.</summary>
    public int AcquireTimeoutSeconds { get; set; } = 30;

    /// <summary>Gets or sets the idle time after which a pooled session is probed before reuse, in seconds; zero probes on every reuse.</summary>
    public int ValidateAfterIdleSeconds { get; set; } = 15;

    /// <summary>Gets or sets the keep-alive interval in seconds for connected sessions; zero disables keep-alives.</summary>
    public int KeepAliveSeconds { get; set; }

    internal IEnumerable<string> GetValidationErrors(string prefix)
    {
        if (MaxSessions is < 1 or > 256)
            yield return $"{prefix}MaxSessions must be between 1 and 256";
        if (MaxIdleSessions < 1 || MaxIdleSessions > MaxSessions)
            yield return $"{prefix}MaxIdleSessions must be between 1 and MaxSessions";
        if (IdleLifetimeSeconds is < 1 or > 86_400)
            yield return $"{prefix}IdleLifetimeSeconds must be between 1 and 86400";
        if (AcquireTimeoutSeconds is < 1 or > 3_600)
            yield return $"{prefix}AcquireTimeoutSeconds must be between 1 and 3600";
        if (ValidateAfterIdleSeconds is < 0 or > 86_400)
            yield return $"{prefix}ValidateAfterIdleSeconds must be between 0 and 86400";
        if (KeepAliveSeconds is < 0 or > 3_600)
            yield return $"{prefix}KeepAliveSeconds must be between 0 and 3600";
    }
}

/// <summary>Speed limits for one connection, shared by all of its concurrent transfers.</summary>
public sealed class StorageTransferLimitsConfig
{
    /// <summary>Gets or sets the maximum upload speed in bytes per second; null or zero means unlimited.</summary>
    public long? MaxUploadBytesPerSecond { get; set; }

    /// <summary>Gets or sets the maximum download speed in bytes per second; null or zero means unlimited.</summary>
    public long? MaxDownloadBytesPerSecond { get; set; }

    internal IEnumerable<string> GetValidationErrors(string prefix)
    {
        if (MaxUploadBytesPerSecond < 0)
            yield return $"{prefix}MaxUploadBytesPerSecond cannot be negative";
        if (MaxDownloadBytesPerSecond < 0)
            yield return $"{prefix}MaxDownloadBytesPerSecond cannot be negative";
    }
}
