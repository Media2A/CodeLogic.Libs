using System.Net;
using CL.Storage.Configuration;
using CL.Storage.Providers.WebDav;
using Xunit;

namespace Storage.Tests;

/// <summary>Covers WebDAV authentication, TLS, and connection settings.</summary>
public sealed class WebDavOptionsTests
{
    [Theory]
    [InlineData(WebDavAuthenticationMode.Digest)]
    [InlineData(WebDavAuthenticationMode.Ntlm)]
    [InlineData(WebDavAuthenticationMode.Negotiate)]
    public void Challenge_schemes_require_username_and_password(WebDavAuthenticationMode mode)
    {
        var config = Valid(c => { c.AuthenticationMode = mode; c.Username = null; c.Password = null; });

        Assert.Contains(config.Validate().Errors, error => error == $"{mode} authentication requires Username and Password");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2000)]
    public void Connection_limit_is_bounded(int limit)
    {
        Assert.False(Valid(c => c.MaxConnectionsPerServer = limit).Validate().IsValid);
    }

    [Fact]
    public void Relative_client_certificate_path_is_rejected()
    {
        Assert.False(Valid(c => c.ClientCertificatePath = "certs/client.pfx").Validate().IsValid);
    }

    [Fact]
    public void Basic_credentials_are_sent_up_front()
    {
        using var http = WebDavStorageBackendFactory.CreateHttpClient(Valid(_ => { }), new Uri("https://dav.example/"));

        Assert.Equal("Basic", http.DefaultRequestHeaders.Authorization?.Scheme);
        Assert.Equal(Convert.ToBase64String("u:p"u8.ToArray()), http.DefaultRequestHeaders.Authorization?.Parameter);
    }

    [Fact]
    public void Bearer_token_is_sent_up_front()
    {
        using var http = WebDavStorageBackendFactory.CreateHttpClient(
            Valid(c => { c.AuthenticationMode = WebDavAuthenticationMode.BearerToken; c.BearerToken = "t0ken"; }),
            new Uri("https://dav.example/"));

        Assert.Equal("Bearer", http.DefaultRequestHeaders.Authorization?.Scheme);
        Assert.Equal("t0ken", http.DefaultRequestHeaders.Authorization?.Parameter);
    }

    [Fact]
    public void Challenge_schemes_do_not_send_basic_credentials()
    {
        using var http = WebDavStorageBackendFactory.CreateHttpClient(
            Valid(c => c.AuthenticationMode = WebDavAuthenticationMode.Digest),
            new Uri("https://dav.example/"));

        Assert.Null(http.DefaultRequestHeaders.Authorization);
    }

    private static WebDavConnectionConfig Valid(Action<WebDavConnectionConfig> configure)
    {
        var config = new WebDavConnectionConfig { Endpoint = "https://dav.example/", Username = "u", Password = "p" };
        configure(config);
        return config;
    }
}
