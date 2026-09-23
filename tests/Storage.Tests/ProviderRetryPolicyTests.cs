using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Providers;
using CodeLogic.Core.Results;
using Xunit;

namespace Storage.Tests;

/// <summary>
/// Covers automatic retry of transient provider failures: which failures and operations are retried,
/// how uploads are replayed, and how backoff honours server-requested delays.
/// </summary>
public sealed class ProviderRetryPolicyTests
{
    [Fact]
    public async Task Retries_transient_failures_until_success()
    {
        var (policy, delays, observer) = Create(new StorageRetryConfig { RetryCount = 3 });
        var calls = 0;

        var result = await policy.ExecuteAsync("Op", RetryKind.Idempotent, (_, _) =>
            Task.FromResult(++calls < 3 ? Result.Failure(StorageErrors.ConnectionLost("lost")) : Result.Success()),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, calls);
        Assert.Equal(2, delays.Count);
        Assert.Equal([1, 2], observer.Attempts);
    }

    [Fact]
    public async Task Gives_up_after_the_configured_retry_count()
    {
        var (policy, _, _) = Create(new StorageRetryConfig { RetryCount = 2 });
        var calls = 0;

        var result = await policy.ExecuteAsync("Op", RetryKind.Idempotent, (_, _) =>
        {
            calls++;
            return Task.FromResult(Result.Failure(StorageErrors.Timeout("slow")));
        }, CancellationToken.None);

        Assert.Equal(StorageErrors.TimeoutCode, result.Error!.Code);
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData(StorageErrors.AuthenticationFailedCode)]
    [InlineData(StorageErrors.PermissionDeniedCode)]
    [InlineData(StorageErrors.NotFoundCode)]
    [InlineData(StorageErrors.HostKeyRejectedCode)]
    [InlineData(StorageErrors.QuotaExceededCode)]
    public async Task Never_retries_permanent_failures(string code)
    {
        var (policy, delays, _) = Create(new StorageRetryConfig { RetryCount = 5 });
        var calls = 0;
        var error = code switch
        {
            StorageErrors.AuthenticationFailedCode => StorageErrors.AuthenticationFailed("x"),
            StorageErrors.PermissionDeniedCode => StorageErrors.PermissionDenied("x"),
            StorageErrors.HostKeyRejectedCode => StorageErrors.HostKeyRejected("x"),
            StorageErrors.QuotaExceededCode => StorageErrors.QuotaExceeded("x"),
            _ => StorageErrors.NotFound("x")
        };

        await policy.ExecuteAsync("Op", RetryKind.Idempotent, (_, _) =>
        {
            calls++;
            return Task.FromResult(Result.Failure(error));
        }, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Non_idempotent_operations_are_retried_only_when_enabled()
    {
        var (disabled, _, _) = Create(new StorageRetryConfig { RetryCount = 3 });
        var (enabled, _, _) = Create(new StorageRetryConfig { RetryCount = 3, RetryNonIdempotent = true });
        var disabledCalls = 0;
        var enabledCalls = 0;

        await disabled.ExecuteAsync("Delete", RetryKind.NonIdempotent, (_, _) =>
        {
            disabledCalls++;
            return Task.FromResult(Result.Failure(StorageErrors.ConnectionLost("lost")));
        }, CancellationToken.None);
        await enabled.ExecuteAsync("Delete", RetryKind.NonIdempotent, (_, _) =>
            Task.FromResult(++enabledCalls < 2 ? Result.Failure(StorageErrors.ConnectionLost("lost")) : Result.Success()),
            CancellationToken.None);

        Assert.Equal(1, disabledCalls);
        Assert.Equal(2, enabledCalls);
    }

    [Fact]
    public async Task Upload_replays_a_seekable_source_from_its_start_position()
    {
        var (policy, _, _) = Create(new StorageRetryConfig { RetryCount = 2 });
        await using var source = new MemoryStream([1, 2, 3, 4, 5]);
        source.Position = 1;
        var seen = new List<byte[]>();

        var result = await policy.ExecuteUploadAsync<int>("Upload", source, async _ =>
        {
            var buffer = new byte[16];
            var read = await source.ReadAsync(buffer);
            seen.Add(buffer[..read]);
            return seen.Count < 2 ? Result<int>.Failure(StorageErrors.ConnectionLost("lost")) : Result<int>.Success(read);
        }, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, seen.Count);
        Assert.All(seen, bytes => Assert.Equal(new byte[] { 2, 3, 4, 5 }, bytes));
    }

    [Fact]
    public async Task Upload_from_a_non_seekable_source_is_attempted_once()
    {
        var (policy, _, _) = Create(new StorageRetryConfig { RetryCount = 3 });
        await using var source = new NonSeekableStream();
        var calls = 0;

        await policy.ExecuteUploadAsync<int>("Upload", source, _ =>
        {
            calls++;
            return Task.FromResult(Result<int>.Failure(StorageErrors.ConnectionLost("lost")));
        }, CancellationToken.None);

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Backoff_grows_exponentially_and_is_clamped()
    {
        var (policy, _, _) = Create(new StorageRetryConfig { BaseDelayMs = 100, MaxDelayMs = 1_000 }, jitter: 0.5);

        Assert.Equal(TimeSpan.FromMilliseconds(100), policy.ComputeDelay(0, null));
        Assert.Equal(TimeSpan.FromMilliseconds(200), policy.ComputeDelay(1, null));
        Assert.Equal(TimeSpan.FromMilliseconds(400), policy.ComputeDelay(2, null));
        Assert.Equal(TimeSpan.FromMilliseconds(1_000), policy.ComputeDelay(8, null));
    }

    [Fact]
    public void Server_retry_after_overrides_a_shorter_backoff_but_not_the_maximum()
    {
        var (policy, _, _) = Create(new StorageRetryConfig { BaseDelayMs = 100, MaxDelayMs = 5_000 }, jitter: 0.5);

        Assert.Equal(TimeSpan.FromSeconds(2), policy.ComputeDelay(0, StorageErrors.ServerBusy("busy", "retryAfterMs=2000")));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.ComputeDelay(0, StorageErrors.ServerBusy("busy", "retryAfterMs=60000")));
    }

    [Fact]
    public async Task Cancellation_during_backoff_stops_retrying()
    {
        using var cancellation = new CancellationTokenSource();
        var policy = new ProviderRetryPolicy(
            new StorageRetryConfig { RetryCount = 5 },
            "c",
            StorageProvider.Ftp,
            delay: (_, token) =>
            {
                cancellation.Cancel();
                return Task.Delay(Timeout.Infinite, token);
            });
        var calls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => policy.ExecuteAsync("Op", RetryKind.Idempotent, (_, _) =>
        {
            calls++;
            return Task.FromResult(Result.Failure(StorageErrors.ConnectionLost("lost")));
        }, cancellation.Token));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(-1, 100, 1000)]
    [InlineData(11, 100, 1000)]
    [InlineData(3, 0, 1000)]
    [InlineData(3, 500, 100)]
    public void Invalid_retry_settings_are_rejected(int count, int baseDelay, int maxDelay)
    {
        var config = new StorageRetryConfig { RetryCount = count, BaseDelayMs = baseDelay, MaxDelayMs = maxDelay };

        Assert.NotEmpty(config.GetValidationErrors("Retry."));
    }

    private static (ProviderRetryPolicy Policy, List<TimeSpan> Delays, RecordingObserver Observer) Create(
        StorageRetryConfig config,
        double jitter = 0.5)
    {
        var delays = new List<TimeSpan>();
        var observer = new RecordingObserver();
        var policy = new ProviderRetryPolicy(
            config,
            "c",
            StorageProvider.Ftp,
            observer,
            (delay, _) => { delays.Add(delay); return Task.CompletedTask; },
            () => jitter);
        return (policy, delays, observer);
    }

    private sealed class RecordingObserver : IStorageConnectionObserver
    {
        public List<int> Attempts { get; } = [];
        public void SessionOpened(string connectionId, StorageProvider provider) { }
        public void SessionFaulted(string connectionId, StorageProvider provider, string operation, Error error) { }
        public void Retrying(string connectionId, StorageProvider provider, string operation, int attempt, TimeSpan delay, Error error) =>
            Attempts.Add(attempt);
    }

    private sealed class NonSeekableStream : MemoryStream
    {
        public override bool CanSeek => false;
    }
}
