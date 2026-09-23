using CL.Storage.Configuration;
using Xunit;

namespace Storage.Tests;

/// <summary>Covers the shared proxy settings every remote provider accepts.</summary>
public sealed class ProxyConfigTests
{
    [Fact]
    public void Disabled_proxy_needs_no_host_and_builds_no_web_proxy()
    {
        var proxy = new StorageProxyConfig();

        Assert.Empty(proxy.GetValidationErrors("Proxy."));
        Assert.Null(proxy.ToWebProxy());
    }

    [Theory]
    [InlineData(StorageProxyType.Http, "http://proxy.local:3128/")]
    [InlineData(StorageProxyType.Socks5, "socks5://proxy.local:3128/")]
    [InlineData(StorageProxyType.Socks4, "socks4://proxy.local:3128/")]
    public void Web_proxy_uses_the_scheme_for_the_proxy_type(StorageProxyType type, string expected)
    {
        var proxy = new StorageProxyConfig { Type = type, Host = "proxy.local", Port = 3128 }.ToWebProxy();

        Assert.Equal(new Uri(expected), proxy!.GetProxy(new Uri("https://storage.example")));
    }

    [Fact]
    public void Proxy_credentials_are_attached()
    {
        var proxy = new StorageProxyConfig { Type = StorageProxyType.Http, Host = "p", Port = 1, Username = "u", Password = "pw" }.ToWebProxy();

        var credential = proxy!.Credentials!.GetCredential(new Uri("http://p:1"), "Basic");
        Assert.Equal("u", credential!.UserName);
        Assert.Equal("pw", credential.Password);
    }

    [Theory]
    [InlineData(StorageProxyType.Http, "", 3128, "Host")]
    [InlineData(StorageProxyType.Socks5, "p", 0, "Port")]
    [InlineData(StorageProxyType.Socks5, "p", 70000, "Port")]
    public void Enabled_proxy_requires_host_and_port(StorageProxyType type, string host, int port, string field)
    {
        var errors = new StorageProxyConfig { Type = type, Host = host, Port = port }.GetValidationErrors("Proxy.");

        Assert.Contains(errors, error => error.StartsWith($"Proxy.{field}", StringComparison.Ordinal));
    }

    [Fact]
    public void Socks4_rejects_a_password_it_cannot_send()
    {
        var errors = new StorageProxyConfig { Type = StorageProxyType.Socks4, Host = "p", Port = 1, Password = "x" }.GetValidationErrors("Proxy.");

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Proxy_errors_surface_through_connection_validation()
    {
        var config = new SftpConnectionConfig
        {
            Host = "h",
            Username = "u",
            Password = "p",
            AutoAcceptHostKey = true,
            Proxy = new StorageProxyConfig { Type = StorageProxyType.Socks5 }
        };

        Assert.Contains(config.Validate().Errors, error => error.StartsWith("Proxy.", StringComparison.Ordinal));
    }
}
