using CL.Storage.Configuration;
using CL.Storage.Providers.GoogleCloud;
using Xunit;

namespace Storage.Tests;

/// <summary>Covers custom endpoints for Google Cloud Storage and Swift (private endpoints, emulators, TempAuth).</summary>
public sealed class EndpointConfigTests
{
    [Theory]
    [InlineData("http://127.0.0.1:4443", "http://127.0.0.1:4443/storage/v1/")]
    [InlineData("https://gcs.internal.example/", "https://gcs.internal.example/storage/v1/")]
    [InlineData("https://gcs.internal.example/storage/v1", "https://gcs.internal.example/storage/v1/")]
    [InlineData("https://gcs.internal.example/storage/v1/", "https://gcs.internal.example/storage/v1/")]
    public void Gcs_service_url_is_normalized_to_the_json_api_root(string serviceUrl, string expected)
    {
        Assert.Equal(expected, GoogleCloudStorageBackendFactory.JsonApiBase(serviceUrl));
    }

    [Fact]
    public void Gcs_http_endpoint_requires_explicit_opt_in()
    {
        var config = new GoogleCloudConnectionConfig { Bucket = "b", ServiceUrl = "http://127.0.0.1:4443", AuthenticationMode = GoogleCloudAuthenticationMode.Anonymous };
        Assert.Contains(config.Validate().Errors, error => error.Contains("AllowInsecureHttp", StringComparison.Ordinal));

        config.AllowInsecureHttp = true;
        Assert.True(config.Validate().IsValid);
    }

    [Theory]
    [InlineData("ftp://example.com")]
    [InlineData("https://user:pw@example.com")]
    [InlineData("https://example.com/?q=1")]
    public void Gcs_rejects_unsafe_service_urls(string serviceUrl)
    {
        var config = new GoogleCloudConnectionConfig { Bucket = "b", ServiceUrl = serviceUrl, AllowInsecureHttp = true };
        Assert.False(config.Validate().IsValid);
    }

    [Fact]
    public void Swift_tempauth_requires_url_user_and_key()
    {
        var config = new SwiftConnectionConfig { Container = "c", AuthenticationMode = SwiftAuthenticationMode.TempAuthV1 };

        var errors = config.Validate().Errors;

        Assert.Contains(errors, error => error.Contains("AuthenticationUrl", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("Username", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("Password", StringComparison.Ordinal));
    }

    [Fact]
    public void Swift_http_urls_require_explicit_opt_in()
    {
        var config = new SwiftConnectionConfig
        {
            Container = "c",
            AuthenticationMode = SwiftAuthenticationMode.TempAuthV1,
            AuthenticationUrl = "http://127.0.0.1:8080/auth/v1.0",
            Username = "test:tester",
            Password = "testing"
        };
        Assert.False(config.Validate().IsValid);

        config.AllowInsecureHttp = true;
        Assert.True(config.Validate().IsValid);
    }
}

public sealed class GcsTimestampTests
{
    [Theory]
    [InlineData("2026-09-22T10:15:30.123Z")]
    [InlineData("2026-09-22T10:15:30.12Z")]
    [InlineData("2026-09-22T10:15:30Z")]
    [InlineData("2026-09-22T10:15:30.123456789Z")]
    public void Timestamps_with_any_fraction_length_parse(string raw)
    {
        var parsed = CL.Storage.Providers.GoogleCloud.GoogleCloudStorageBackend.ParseTimestamp(raw);

        Assert.Equal(new DateTimeOffset(2026, 9, 22, 10, 15, 30, TimeSpan.Zero), parsed!.Value.AddTicks(-(parsed.Value.Ticks % TimeSpan.TicksPerSecond)));
    }

    [Fact]
    public void Malformed_timestamp_becomes_unknown_instead_of_failing()
    {
        Assert.Null(CL.Storage.Providers.GoogleCloud.GoogleCloudStorageBackend.ParseTimestamp("not a date"));
    }
}
