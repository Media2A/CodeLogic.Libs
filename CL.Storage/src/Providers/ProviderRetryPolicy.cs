using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Providers;

/// <summary>Whether an operation may be repeated after a transient failure.</summary>
internal enum RetryKind
{
    /// <summary>Repeating has the same effect as running once: reads, listings, info, staged uploads.</summary>
    Idempotent,
    /// <summary>The first attempt may have completed before its reply was lost: deletes and moves.</summary>
    NonIdempotent,
    /// <summary>The operation cannot be replayed, for example an upload from a non-seekable stream.</summary>
    Never
}

/// <summary>Receives connection lifecycle notifications from provider backends.</summary>
internal interface IStorageConnectionObserver
{
    void SessionOpened(string connectionId, StorageProvider provider);
    void SessionFaulted(string connectionId, StorageProvider provider, string operation, Error error);
    void Retrying(string connectionId, StorageProvider provider, string operation, int attempt, TimeSpan delay, Error error);
}

/// <summary>
/// One attempt of a provider operation. It is the ambient attempt (an <see cref="AsyncLocal{T}"/>) for everything the
/// attempt awaits, so a transport watch can attribute what happens on a connection to the attempt that used it rather
/// than to the whole backend.
/// </summary>
internal sealed class ProviderAttempt
{
    private static readonly AsyncLocal<ProviderAttempt?> Ambient = new();
    private int _clientCertificateRefused;

    private ProviderAttempt(DateTimeOffset started) => Started = started;

    /// <summary>Gets when the attempt started.</summary>
    public DateTimeOffset Started { get; }

    /// <summary>Gets the attempt the calling code runs in, if any.</summary>
    public static ProviderAttempt? Current => Ambient.Value;

    /// <summary>Starts an attempt and makes it the ambient one for the calling async method and what it awaits.</summary>
    public static ProviderAttempt Begin()
    {
        var attempt = new ProviderAttempt(DateTimeOffset.UtcNow);
        Ambient.Value = attempt;
        return attempt;
    }

    /// <summary>Creates an attempt that is not ambient (for callers that only have a start time).</summary>
    public static ProviderAttempt At(DateTimeOffset started) => new(started);

    /// <summary>Gets whether a connection this attempt sent a request on was refused for its client certificate.</summary>
    public bool ClientCertificateRefused => Volatile.Read(ref _clientCertificateRefused) != 0;

    /// <summary>Records that a connection this attempt used was refused for its client certificate.</summary>
    public void RecordClientCertificateRefusal() => Volatile.Write(ref _clientCertificateRefused, 1);
}

/// <summary>Repeats provider operations that fail transiently, with exponential backoff and jitter.</summary>
/// <remarks>
/// The backoff mirrors the database libraries: base × 2^attempt, ±50% jitter, clamped to the configured
/// maximum. A server-supplied <c>Retry-After</c> overrides the computed delay when it is longer.
/// </remarks>
internal sealed class ProviderRetryPolicy
{
    private readonly int _retryCount;
    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _maxDelay;
    private readonly bool _retryNonIdempotent;
    private readonly string _connectionId;
    private readonly StorageProvider _provider;
    private readonly IStorageConnectionObserver? _observer;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<double> _jitter;

    public ProviderRetryPolicy(
        StorageRetryConfig? config,
        string connectionId,
        StorageProvider provider,
        IStorageConnectionObserver? observer = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<double>? jitter = null)
    {
        config ??= new StorageRetryConfig();
        _retryCount = Math.Max(0, config.RetryCount);
        _baseDelay = TimeSpan.FromMilliseconds(Math.Max(1, config.BaseDelayMs));
        _maxDelay = TimeSpan.FromMilliseconds(Math.Max(config.BaseDelayMs, config.MaxDelayMs));
        _retryNonIdempotent = config.RetryNonIdempotent;
        _connectionId = connectionId;
        _provider = provider;
        _observer = observer;
        _delay = delay ?? Task.Delay;
        _jitter = jitter ?? Random.Shared.NextDouble;
    }

    /// <summary>A policy that never retries, for backends constructed without configuration.</summary>
    public static ProviderRetryPolicy None(string connectionId, StorageProvider provider) =>
        new(new StorageRetryConfig { RetryCount = 0 }, connectionId, provider);

    /// <summary>
    /// Gets or sets a hook that adds context to every failed attempt, such as the certificate a TLS failure
    /// refused; it receives the error and the attempt (when it started, and what its own connections recorded).
    /// </summary>
    public Func<Error, ProviderAttempt, Error>? Enrich { get; set; }

    public Task<Result> ExecuteAsync(
        string operation,
        RetryKind kind,
        Func<int, CancellationToken, Task<Result>> attempt,
        CancellationToken cancellationToken) =>
        ExecuteCoreAsync(operation, kind, async (number, token) =>
        {
            var current = ProviderAttempt.Begin();
            var result = await attempt(number, token).ConfigureAwait(false);
            return result.IsFailure && Enrich is { } enrich ? Result.Failure(enrich(result.Error!, current)) : result;
        }, static result => result.Error, cancellationToken);

    public Task<Result<T>> ExecuteAsync<T>(
        string operation,
        RetryKind kind,
        Func<int, CancellationToken, Task<Result<T>>> attempt,
        CancellationToken cancellationToken) =>
        ExecuteCoreAsync(operation, kind, async (number, token) =>
        {
            var current = ProviderAttempt.Begin();
            var result = await attempt(number, token).ConfigureAwait(false);
            return result.IsFailure && Enrich is { } enrich ? Result<T>.Failure(enrich(result.Error!, current)) : result;
        }, static result => result.Error, cancellationToken);

    /// <summary>Runs an upload, replaying the source from its starting position on retry.</summary>
    /// <remarks>A non-seekable source cannot be replayed, so its upload is attempted exactly once.</remarks>
    public Task<Result<T>> ExecuteUploadAsync<T>(
        string operation,
        Stream source,
        Func<CancellationToken, Task<Result<T>>> upload,
        CancellationToken cancellationToken)
    {
        if (!source.CanSeek)
            return UploadOnceAsync(upload, cancellationToken);
        var start = source.Position;
        return ExecuteAsync(
            operation,
            RetryKind.Idempotent,
            (attempt, token) =>
            {
                if (attempt > 0) source.Position = start;
                return upload(token);
            },
            cancellationToken);
    }

    /// <summary>A single attempt, whose failure is still explained by <see cref="Enrich"/> like a retried one's.</summary>
    private async Task<Result<T>> UploadOnceAsync<T>(Func<CancellationToken, Task<Result<T>>> upload, CancellationToken cancellationToken)
    {
        var current = ProviderAttempt.Begin();
        var result = await upload(cancellationToken).ConfigureAwait(false);
        return result.IsFailure && Enrich is { } enrich ? Result<T>.Failure(enrich(result.Error!, current)) : result;
    }

    /// <summary>Computes the delay before retry number <paramref name="attempt"/> (zero-based).</summary>
    internal TimeSpan ComputeDelay(int attempt, Error? error)
    {
        var exponential = _baseDelay.TotalMilliseconds * Math.Pow(2, Math.Min(attempt, 20));
        var jittered = exponential * (0.5 + _jitter());
        var delay = TimeSpan.FromMilliseconds(Math.Clamp(jittered, 1, _maxDelay.TotalMilliseconds));
        if (StorageErrorInfo.TryGetRetryAfter(error, out var requested) && requested > delay)
            delay = requested <= _maxDelay ? requested : _maxDelay;
        return delay;
    }

    private bool MayRetry(RetryKind kind) => kind switch
    {
        RetryKind.Idempotent => true,
        RetryKind.NonIdempotent => _retryNonIdempotent,
        _ => false
    };

    private async Task<TResult> ExecuteCoreAsync<TResult>(
        string operation,
        RetryKind kind,
        Func<int, CancellationToken, Task<TResult>> attempt,
        Func<TResult, Error?> errorOf,
        CancellationToken cancellationToken)
    {
        for (var number = 0; ; number++)
        {
            var result = await attempt(number, cancellationToken).ConfigureAwait(false);
            var error = errorOf(result);
            if (error is null || number >= _retryCount || !MayRetry(kind) || !StorageErrorInfo.IsTransient(error))
                return result;

            var delay = ComputeDelay(number, error);
            try { _observer?.Retrying(_connectionId, _provider, operation, number + 1, delay, error); }
            catch { /* Observers must never change the operation's outcome. */ }
            await _delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }
}
