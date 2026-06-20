using Amazon.S3;
using CL.StorageS3;
using CL.StorageS3.Models;
using CodeLogic;                     // Libraries, CodeLogicOptions
using Xunit;

namespace StorageS3.Tests;

// ── CL.StorageS3 tests ────────────────────────────────────────────────────────────
// HYBRID strategy:
//   • Config validation, the AmazonS3Client factory, and the option/model factories are
//     pure, in-memory components — they are exercised directly with no runtime boot and
//     no network access (constructing an AmazonS3Client does NOT make a network call).
//   • Real bucket/object operations require a live S3/MinIO endpoint, so those tests are
//     env-gated and SKIP unless the CL_S3_TEST_* environment variables are present.

// ── S3ConnectionConfig (offline, direct instantiation) ─────────────────────────────

public sealed class S3ConnectionConfigTests
{
    private static S3ConnectionConfig Valid() => new()
    {
        ConnectionId = "Default",
        AccessKey = "ak",
        SecretKey = "sk",
        ServiceUrl = "http://localhost:9000",
    };

    [Fact]
    public void IsValid_true_when_all_required_fields_present()
    {
        Assert.True(Valid().IsValid());
    }

    [Theory]
    [InlineData("", "sk", "http://localhost:9000")]
    [InlineData("ak", "", "http://localhost:9000")]
    [InlineData("ak", "sk", "")]
    public void IsValid_false_when_any_required_field_missing(string access, string secret, string url)
    {
        var cfg = new S3ConnectionConfig { AccessKey = access, SecretKey = secret, ServiceUrl = url };
        Assert.False(cfg.IsValid());
    }

    [Fact]
    public void BuildClient_returns_non_null_client_without_network_call()
    {
        var cfg = Valid();
        AmazonS3Client? client = null;
        var ex = Record.Exception(() => client = cfg.BuildClient());
        Assert.Null(ex);
        Assert.NotNull(client);
        client!.Dispose();
    }

    [Fact]
    public void BuildClient_works_without_service_url_using_region()
    {
        // No ServiceUrl -> the client is built from the Region endpoint instead.
        var cfg = new S3ConnectionConfig { AccessKey = "ak", SecretKey = "sk", ServiceUrl = "", Region = "us-east-1" };
        using var client = cfg.BuildClient();
        Assert.NotNull(client);
    }

    [Fact]
    public void Defaults_match_source()
    {
        var cfg = new S3ConnectionConfig();
        Assert.True(cfg.ForcePathStyle);          // default true (MinIO-friendly)
        Assert.False(cfg.DisablePayloadSigning);  // default false (AWS/MinIO compatible)
        Assert.True(cfg.UseHttps);
        Assert.Equal(30, cfg.TimeoutSeconds);
        Assert.Equal(3, cfg.MaxRetries);
        Assert.Equal("us-east-1", cfg.Region);
        Assert.Equal("Default", cfg.ConnectionId);
    }
}

// ── StorageS3Config.Validate (offline) ─────────────────────────────────────────────

public sealed class StorageS3ConfigValidationTests
{
    private static S3ConnectionConfig ValidConn(string id = "Default") => new()
    {
        ConnectionId = id,
        AccessKey = "ak",
        SecretKey = "sk",
        ServiceUrl = "http://localhost:9000",
    };

    [Fact]
    public void Enabled_with_zero_connections_fails()
    {
        var cfg = new StorageS3Config { Enabled = true, Connections = [] };
        var result = cfg.Validate();
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Enabled_with_one_valid_connection_passes()
    {
        var cfg = new StorageS3Config { Enabled = true, Connections = [ValidConn()] };
        var result = cfg.Validate();
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Duplicate_connection_ids_fail()
    {
        var cfg = new StorageS3Config
        {
            Enabled = true,
            Connections = [ValidConn("dup"), ValidConn("dup")],
        };
        var result = cfg.Validate();
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Duplicate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Disabled_with_zero_connections_passes()
    {
        // The "at least one connection" rule only applies when Enabled.
        var cfg = new StorageS3Config { Enabled = false, Connections = [] };
        Assert.True(cfg.Validate().IsValid);
    }

    [Fact]
    public void Connection_missing_required_fields_fails()
    {
        var cfg = new StorageS3Config
        {
            Enabled = true,
            Connections = [new S3ConnectionConfig { ConnectionId = "x" }], // no keys / url
        };
        Assert.False(cfg.Validate().IsValid);
    }
}

// ── Option / model factories and POCOs (offline) ───────────────────────────────────

public sealed class OptionsAndModelTests
{
    [Fact]
    public void UploadOptions_Default_has_documented_defaults()
    {
        var o = UploadOptions.Default();
        Assert.NotNull(o);
        Assert.Equal("", o.ContentType);
        Assert.Equal("", o.StorageClass);
        Assert.Equal("", o.CacheControl);
        Assert.Equal("", o.ContentDisposition);
        Assert.False(o.MakePublic);
        Assert.NotNull(o.Metadata);
        Assert.Empty(o.Metadata);
    }

    [Fact]
    public void DownloadOptions_Default_has_documented_defaults()
    {
        var o = DownloadOptions.Default();
        Assert.NotNull(o);
        Assert.Null(o.RangeStart);
        Assert.Null(o.RangeEnd);
        Assert.Null(o.VersionId);
    }

    [Fact]
    public void S3ObjectInfo_round_trips_properties()
    {
        var info = new S3ObjectInfo
        {
            Key = "k",
            BucketName = "b",
            Size = 123,
            ETag = "etag",
            ContentType = "text/plain",
        };
        Assert.Equal("k", info.Key);
        Assert.Equal("b", info.BucketName);
        Assert.Equal(123, info.Size);
        Assert.Equal("etag", info.ETag);
        Assert.Equal("text/plain", info.ContentType);
        Assert.NotNull(info.Metadata);
    }

    [Fact]
    public void BucketInfo_round_trips_properties()
    {
        var b = new BucketInfo { Name = "bucket", Region = "us-east-1" };
        Assert.Equal("bucket", b.Name);
        Assert.Equal("us-east-1", b.Region);
    }

    [Fact]
    public void ListObjectsResult_count_reflects_objects()
    {
        var r = new ListObjectsResult
        {
            Objects = [new S3ObjectInfo { Key = "a" }, new S3ObjectInfo { Key = "b" }],
            IsTruncated = true,
        };
        Assert.Equal(2, r.Count);
        Assert.True(r.IsTruncated);
        Assert.NotNull(r.CommonPrefixes);
    }
}

// ── Gated live S3 / MinIO test ─────────────────────────────────────────────────────
// Boots the real CodeLogic runtime (process-wide singleton), so it lives in its own
// class behind the shared "codelogic" collection. It SKIPS unless the full set of
// CL_S3_TEST_* environment variables is provided.

public sealed class S3RuntimeFixture : IAsyncLifetime
{
    public string TempDir { get; } =
        Path.Combine(Path.GetTempPath(), "cl_s3_test_" + Guid.NewGuid().ToString("N"));

    public StorageS3Library? Library { get; private set; }

    public bool Booted { get; private set; }

    public async Task InitializeAsync()
    {
        // Only boot the runtime when the live S3 test is actually configured to run.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CL_S3_TEST_SERVICEURL")))
            return;

        Directory.CreateDirectory(TempDir);

        var init = await CodeLogic.CodeLogic.InitializeAsync(o =>
        {
            o.FrameworkRootPath = TempDir;
            o.AppVersion = "1.0.0";
            o.HandleShutdownSignals = false;
        });
        if (!init.Success)
            throw new InvalidOperationException($"CodeLogic init failed: {init.Message}");

        var serviceUrl = Environment.GetEnvironmentVariable("CL_S3_TEST_SERVICEURL")!;
        var accessKey = Environment.GetEnvironmentVariable("CL_S3_TEST_ACCESSKEY") ?? "";
        var secretKey = Environment.GetEnvironmentVariable("CL_S3_TEST_SECRETKEY") ?? "";

        var cfgDir = Path.Combine(TempDir, "Libraries", "CL.StorageS3");
        Directory.CreateDirectory(cfgDir);
        File.WriteAllText(Path.Combine(cfgDir, "config.storages3.json"), $$"""
        {
          "enabled": true,
          "connections": [
            {
              "connectionId": "Default",
              "accessKey": "{{accessKey}}",
              "secretKey": "{{secretKey}}",
              "serviceUrl": "{{serviceUrl}}",
              "region": "us-east-1",
              "forcePathStyle": true,
              "useHttps": false,
              "timeoutSeconds": 30,
              "maxRetries": 3
            }
          ]
        }
        """);

        await Libraries.LoadAsync<StorageS3Library>();
        await CodeLogic.CodeLogic.ConfigureAsync();
        await CodeLogic.CodeLogic.StartAsync();

        Library = Libraries.Get<StorageS3Library>()
            ?? throw new InvalidOperationException("StorageS3Library not available after start.");
        Booted = true;
    }

    public async Task DisposeAsync()
    {
        if (Booted)
        {
            try { await CodeLogic.CodeLogic.StopAsync(); } catch { /* best effort */ }
        }
        try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, recursive: true); }
        catch { /* ignore lingering files on Windows */ }
    }
}

[CollectionDefinition("codelogic")]
public sealed class CodeLogicCollection : ICollectionFixture<S3RuntimeFixture> { }

/// <summary>
/// A <see cref="FactAttribute"/> that statically skips the test unless the named
/// environment variable is set. xUnit 2.9.3 has no runtime <c>Assert.Skip</c>, so the
/// skip decision is made here (at discovery time) and reported as a proper "Skipped".
/// </summary>
internal sealed class FactRequiresEnvAttribute : FactAttribute
{
    public FactRequiresEnvAttribute(string envVar, string reason)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envVar)))
            Skip = reason;
    }
}

[Collection("codelogic")]
public sealed class LiveS3Tests
{
    private readonly S3RuntimeFixture _fx;

    public LiveS3Tests(S3RuntimeFixture fx) => _fx = fx;

    [FactRequiresEnv("CL_S3_TEST_SERVICEURL", "set CL_S3_TEST_* to run live S3/MinIO test")]
    public async Task Put_get_delete_round_trips_object()
    {
        var bucket = Environment.GetEnvironmentVariable("CL_S3_TEST_BUCKET")
                     ?? throw new InvalidOperationException("CL_S3_TEST_BUCKET is required");

        var lib = _fx.Library ?? throw new InvalidOperationException("Runtime not booted.");
        var svc = lib.DefaultService;

        var key = "cl-storages3-test/" + Guid.NewGuid().ToString("N") + ".bin";
        var payload = System.Text.Encoding.UTF8.GetBytes("CL.StorageS3 integration payload " + Guid.NewGuid());

        // Upload
        var put = await svc.PutObjectAsync(bucket, key, payload, UploadOptions.Default(), CancellationToken.None);
        Assert.True(put.IsSuccess, put.Error?.ToString());
        Assert.NotNull(put.Value);
        Assert.Equal(key, put.Value!.Key);

        // ObjectExists returns a plain bool (not Result<T>)
        Assert.True(await svc.ObjectExistsAsync(bucket, key, CancellationToken.None));

        // Download round-trips the bytes
        var get = await svc.GetObjectAsync(bucket, key, DownloadOptions.Default(), CancellationToken.None);
        Assert.True(get.IsSuccess, get.Error?.ToString());
        Assert.Equal(payload, get.Value);

        // Delete
        var del = await svc.DeleteObjectAsync(bucket, key, CancellationToken.None);
        Assert.True(del.IsSuccess, del.Error?.ToString());

        Assert.False(await svc.ObjectExistsAsync(bucket, key, CancellationToken.None));
    }
}
