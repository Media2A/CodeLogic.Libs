using System.Security.Cryptography;
using System.Text;
using CL.Storage.Configuration;
using CL.Storage.Providers.Sftp;
using Renci.SshNet;
using Xunit;

namespace Storage.Tests;

/// <summary>Covers SFTP host-key trust, known_hosts parsing, authentication method selection, and validation.</summary>
public sealed class SftpOptionsTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(51);
    private static readonly byte[] OtherKey = RandomNumberGenerator.GetBytes(51);
    private static string KeyText => Convert.ToBase64String(Key);

    [Theory]
    [InlineData("files.example", 22, "files.example")]
    [InlineData("files.example", 2222, "[files.example]:2222")]
    public void Host_token_follows_openssh_port_convention(string host, int port, string expected)
    {
        Assert.Equal(expected, SshHostTrust.HostToken(host, port));
    }

    [Theory]
    [InlineData("files.example", true)]
    [InlineData("other.example,files.example", true)]
    [InlineData("*.example", true)]
    [InlineData("files.exampl?", true)]
    [InlineData("*.example,!files.example", false)]
    [InlineData("[files.example]:2222", false)]
    [InlineData("nope.example", false)]
    public void Known_hosts_patterns_match_like_openssh(string patterns, bool trusted)
    {
        Assert.Equal(trusted, TrustFor($"{patterns} ssh-ed25519 {KeyText}", "files.example", 22).IsTrusted(Key, "SHA256:x"));
    }

    [Fact]
    public void Non_default_port_needs_a_bracketed_entry()
    {
        Assert.True(TrustFor($"[files.example]:2222 ssh-ed25519 {KeyText}", "files.example", 2222).IsTrusted(Key, "SHA256:x"));
        Assert.False(TrustFor($"files.example ssh-ed25519 {KeyText}", "files.example", 2222).IsTrusted(Key, "SHA256:x"));
    }

    [Fact]
    public void Hashed_entries_match_the_hashed_host()
    {
        var salt = RandomNumberGenerator.GetBytes(20);
        var hash = HMACSHA1.HashData(salt, Encoding.UTF8.GetBytes("files.example"));
        var line = $"|1|{Convert.ToBase64String(salt)}|{Convert.ToBase64String(hash)} ssh-ed25519 {KeyText}";

        Assert.True(TrustFor(line, "files.example", 22).IsTrusted(Key, "SHA256:x"));
        Assert.False(TrustFor(line, "other.example", 22).IsTrusted(Key, "SHA256:x"));
    }

    [Fact]
    public void A_different_key_for_the_host_is_not_trusted()
    {
        Assert.False(TrustFor($"files.example ssh-ed25519 {KeyText}", "files.example", 22).IsTrusted(OtherKey, "SHA256:x"));
    }

    [Fact]
    public void Revoked_keys_override_auto_accept_and_pins()
    {
        var path = WriteKnownHosts($"@revoked * ssh-ed25519 {KeyText}");
        try
        {
            var fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(Key)).TrimEnd('=');
            var trust = new SshHostTrust(true, [fingerprint], path, "files.example", 22);
            Assert.False(trust.IsTrusted(Key, fingerprint));
            Assert.True(trust.IsTrusted(OtherKey, "SHA256:other"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Comments_blank_lines_ca_lines_and_garbage_are_ignored()
    {
        var entries = SshHostTrust.Parse(
        [
            "# comment",
            "",
            $"@cert-authority *.example ssh-ed25519 {KeyText}",
            "files.example ssh-ed25519 not-base64!!",
            "truncated-line",
            $"files.example ssh-ed25519 {KeyText} trailing comment"
        ]);

        var entry = Assert.Single(entries);
        Assert.Equal("ssh-ed25519", entry.KeyType);
    }

    [Fact]
    public void Pinned_fingerprint_still_trusts_without_known_hosts()
    {
        var fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(Key)).TrimEnd('=');
        var trust = new SshHostTrust(false, [fingerprint], null, "files.example", 22);

        Assert.True(trust.IsTrusted(Key, Convert.ToBase64String(SHA256.HashData(Key)).TrimEnd('=')));
    }

    [Fact]
    public void Auto_mode_offers_keys_then_password_then_keyboard_interactive()
    {
        var keyPath = WriteTemporaryKey();
        try
        {
            var config = Valid(c =>
            {
                c.AuthenticationMode = SftpAuthenticationMode.Auto;
                c.Password = "pw";
                c.PrivateKeyPath = keyPath;
            });

            var methods = SftpStorageBackendFactory.AuthenticationMethods(config).Select(method => method.GetType()).ToArray();

            Assert.Equal([typeof(PrivateKeyAuthenticationMethod), typeof(PasswordAuthenticationMethod), typeof(KeyboardInteractiveAuthenticationMethod)], methods);
        }
        finally { File.Delete(keyPath); }
    }

    [Fact]
    public void Keyboard_interactive_mode_offers_only_keyboard_interactive()
    {
        var config = Valid(c => { c.AuthenticationMode = SftpAuthenticationMode.KeyboardInteractive; c.Password = "pw"; });

        var method = Assert.Single(SftpStorageBackendFactory.AuthenticationMethods(config));
        Assert.IsType<KeyboardInteractiveAuthenticationMethod>(method);
    }

    [Fact]
    public void Known_hosts_alone_satisfies_host_key_verification()
    {
        var config = Valid(c => { c.HostKeyFingerprints = []; c.AutoAcceptHostKey = false; c.KnownHostsPath = Path.GetFullPath("known_hosts"); });

        Assert.True(config.Validate().IsValid);
    }

    [Theory]
    [InlineData("KeyExchangeAlgorithms", "diffie-hellman-group1-sha0")]
    [InlineData("Ciphers", "rot13")]
    [InlineData("MacAlgorithms", "hmac-md0")]
    [InlineData("HostKeyAlgorithms", "ssh-dsa-unknown")]
    public void Unknown_algorithms_are_rejected_with_the_supported_list(string setting, string name)
    {
        var config = Valid(c =>
        {
            switch (setting)
            {
                case "KeyExchangeAlgorithms": c.KeyExchangeAlgorithms = [name]; break;
                case "Ciphers": c.Ciphers = [name]; break;
                case "MacAlgorithms": c.MacAlgorithms = [name]; break;
                default: c.HostKeyAlgorithms = [name]; break;
            }
        });

        var error = Assert.Single(config.Validate().Errors);
        Assert.StartsWith($"{setting} contains '{name}'", error);
        Assert.Contains("Supported:", error);
    }

    [Fact]
    public void Algorithm_restriction_keeps_only_listed_names_in_listed_order()
    {
        var available = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2, ["c"] = 3 };

        SshAlgorithmNames.Restrict(available, ["c", "a", "missing"]);

        Assert.Equal(["c", "a"], available.Keys);
    }

    [Theory]
    [InlineData("klingon")]
    [InlineData("")]
    public void Unknown_encodings_are_rejected(string encoding)
    {
        Assert.False(Valid(c => c.Encoding = encoding).Validate().IsValid);
    }

    [Fact]
    public void Jump_host_errors_are_prefixed()
    {
        var config = Valid(c => c.JumpHost = new SftpJumpHostConfig());

        Assert.Contains(config.Validate().Errors, error => error == "JumpHost.Host is required");
        Assert.Contains(config.Validate().Errors, error => error.StartsWith("JumpHost.At least one HostKeyFingerprint", StringComparison.Ordinal));
    }

    private static SftpConnectionConfig Valid(Action<SftpConnectionConfig> configure)
    {
        var config = new SftpConnectionConfig { Host = "files.example", Username = "u", Password = "p", AutoAcceptHostKey = true };
        configure(config);
        return config;
    }

    private static SshHostTrust TrustFor(string line, string host, int port)
    {
        var path = WriteKnownHosts(line);
        try { return new SshHostTrust(false, null, path, host, port); }
        finally { File.Delete(path); }
    }

    private static string WriteKnownHosts(string line)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cl-kh-{Guid.NewGuid():N}");
        File.WriteAllText(path, line + "\n");
        return path;
    }

    private static string WriteTemporaryKey()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cl-key-{Guid.NewGuid():N}");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(path, key.ExportECPrivateKeyPem());
        return path;
    }
}
