using System.Security.Cryptography;
using System.Text;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using Renci.SshNet;
using Xunit;

namespace Storage.Integration.Tests;

/// <summary>Live coverage of SFTP authentication, host-key trust, algorithms, and jump hosts.</summary>
public sealed class SftpOptionsLiveTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static SftpConnectionConfig KeyOnly(Action<SftpConnectionConfig>? configure = null) => LiveServers.Sftp(c =>
    {
        c.Password = null;
        c.AuthenticationMode = SftpAuthenticationMode.PrivateKey;
        c.Retry.RetryCount = 0;
        configure?.Invoke(c);
    });

    [SftpFact]
    public async Task Private_key_file_authenticates()
    {
        await using var storage = LiveServers.Create(KeyOnly(c => c.PrivateKeyPath = Fixture("test_ed25519")));
        var health = await storage.CheckHealthAsync();
        Assert.True(health.IsSuccess, health.Error?.ToString());
    }

    [SftpFact]
    public async Task Inline_encrypted_private_key_authenticates()
    {
        await using var storage = LiveServers.Create(KeyOnly(c =>
        {
            c.PrivateKeyContent = File.ReadAllText(Fixture("test_ed25519_encrypted"));
            c.PrivateKeyPassphrase = "cl-test-passphrase";
        }));
        var health = await storage.CheckHealthAsync();
        Assert.True(health.IsSuccess, health.Error?.ToString());
    }

    [SftpFact]
    public async Task Auto_mode_falls_back_to_the_key_when_the_password_is_wrong()
    {
        await using var storage = LiveServers.Create(LiveServers.Sftp(c =>
        {
            c.AuthenticationMode = SftpAuthenticationMode.Auto;
            c.Password = "wrong";
            c.PrivateKeyPath = Fixture("test_ed25519");
        }));
        var health = await storage.CheckHealthAsync();
        Assert.True(health.IsSuccess, health.Error?.ToString());
    }

    [SftpFact]
    public async Task Wrong_key_is_an_authentication_failure()
    {
        var otherKey = Path.Combine(Path.GetTempPath(), $"cl-other-{Guid.NewGuid():N}");
        using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            File.WriteAllText(otherKey, key.ExportECPrivateKeyPem());
        try
        {
            await using var storage = LiveServers.Create(KeyOnly(c => c.PrivateKeyPath = otherKey));
            var health = await storage.CheckHealthAsync();
            Assert.Equal(StorageErrors.AuthenticationFailedCode, health.Error?.Code);
        }
        finally { File.Delete(otherKey); }
    }

    [SftpFact]
    public async Task Known_hosts_entry_trusts_the_server()
    {
        var (keyType, key) = await CaptureHostKeyAsync();
        await WithKnownHostsAsync($"{HostToken()} {keyType} {key}", async path =>
        {
            await using var storage = LiveServers.Create(Strict(path));
            var health = await storage.CheckHealthAsync();
            Assert.True(health.IsSuccess, health.Error?.ToString());
        });
    }

    [SftpFact]
    public async Task Hashed_known_hosts_entry_trusts_the_server()
    {
        var (keyType, key) = await CaptureHostKeyAsync();
        var salt = RandomNumberGenerator.GetBytes(20);
        var hash = HMACSHA1.HashData(salt, Encoding.UTF8.GetBytes(HostToken()));
        await WithKnownHostsAsync($"|1|{Convert.ToBase64String(salt)}|{Convert.ToBase64String(hash)} {keyType} {key}", async path =>
        {
            await using var storage = LiveServers.Create(Strict(path));
            var health = await storage.CheckHealthAsync();
            Assert.True(health.IsSuccess, health.Error?.ToString());
        });
    }

    [SftpFact]
    public async Task Known_hosts_entry_for_another_host_is_rejected()
    {
        var (keyType, key) = await CaptureHostKeyAsync();
        await WithKnownHostsAsync($"other.example {keyType} {key}", async path =>
        {
            await using var storage = LiveServers.Create(Strict(path));
            var health = await storage.CheckHealthAsync();
            Assert.Equal(StorageErrors.HostKeyRejectedCode, health.Error?.Code);
        });
    }

    [SftpFact]
    public async Task Revoked_key_is_rejected_even_when_auto_accept_is_on()
    {
        var (keyType, key) = await CaptureHostKeyAsync();
        await WithKnownHostsAsync($"@revoked * {keyType} {key}", async path =>
        {
            await using var storage = LiveServers.Create(LiveServers.Sftp(c =>
            {
                c.KnownHostsPath = path;
                c.AutoAcceptHostKey = true;
                c.Retry.RetryCount = 0;
            }));
            var health = await storage.CheckHealthAsync();
            Assert.Equal(StorageErrors.HostKeyRejectedCode, health.Error?.Code);
        });
    }

    [SftpFact]
    public async Task Restricted_algorithms_are_negotiated()
    {
        await using var storage = LiveServers.Create(LiveServers.Sftp(c =>
        {
            c.KeyExchangeAlgorithms = ["curve25519-sha256"];
            c.Ciphers = ["aes256-ctr"];
            c.MacAlgorithms = ["hmac-sha2-256"];
        }));
        var health = await storage.CheckHealthAsync();
        Assert.True(health.IsSuccess, health.Error?.ToString());
    }

    [SftpFact]
    public async Task Algorithms_the_server_refuses_fail_to_connect()
    {
        // OpenSSH disables CBC ciphers by default, so no cipher can be agreed.
        await using var storage = LiveServers.Create(LiveServers.Sftp(c =>
        {
            c.Ciphers = ["aes128-cbc"];
            c.Retry.RetryCount = 0;
        }));
        var health = await storage.CheckHealthAsync();
        Assert.Equal(StorageErrors.ConnectionFailedCode, health.Error?.Code);
    }

    [SftpFact]
    public async Task Jump_host_tunnels_to_a_server_only_it_can_reach()
    {
        // "sftp" resolves only inside the Docker network, so the tunnel is the only route.
        await using var storage = LiveServers.Create(ThroughJump());
        await StorageContract.RoundTripAsync(storage);
    }

    [SftpFact]
    public async Task Untrusted_jump_host_key_is_rejected()
    {
        await using var storage = LiveServers.Create(ThroughJump(jump =>
        {
            jump.AutoAcceptHostKey = false;
            jump.HostKeyFingerprints = ["SHA256:" + Convert.ToBase64String(new byte[32]).TrimEnd('=')];
        }));
        var health = await storage.CheckHealthAsync();
        Assert.Equal(StorageErrors.HostKeyRejectedCode, health.Error?.Code);
    }

    [SftpFact]
    public async Task Wrong_jump_host_password_is_an_authentication_failure()
    {
        await using var storage = LiveServers.Create(ThroughJump(jump => jump.Password = "wrong"));
        var health = await storage.CheckHealthAsync();
        Assert.Equal(StorageErrors.AuthenticationFailedCode, health.Error?.Code);
    }

    private static SftpConnectionConfig ThroughJump(Action<SftpJumpHostConfig>? configure = null)
    {
        var jump = new SftpJumpHostConfig
        {
            Host = LiveServers.Env("CL_STORAGE_TEST_SSH_JUMP_HOST") ?? "127.0.0.1",
            Port = int.Parse(LiveServers.Env("CL_STORAGE_TEST_SSH_JUMP_PORT") ?? "2023"),
            Username = "jump",
            Password = "jump-pw",
            AutoAcceptHostKey = true
        };
        configure?.Invoke(jump);
        return LiveServers.Sftp(c =>
        {
            c.Host = "sftp";
            c.Port = 22;
            c.JumpHost = jump;
            c.Retry.RetryCount = 0;
        });
    }

    private static SftpConnectionConfig Strict(string knownHostsPath) => LiveServers.Sftp(c =>
    {
        c.AutoAcceptHostKey = false;
        c.KnownHostsPath = knownHostsPath;
        c.Retry.RetryCount = 0;
    });

    private static string HostToken()
    {
        var config = LiveServers.Sftp();
        return config.Port == 22 ? config.Host : $"[{config.Host}]:{config.Port}";
    }

    private static async Task<(string KeyType, string Key)> CaptureHostKeyAsync()
    {
        var config = LiveServers.Sftp();
        using var client = new SftpClient(config.Host, config.Port, config.Username, config.Password ?? string.Empty);
        string? keyType = null;
        byte[]? key = null;
        client.HostKeyReceived += (_, e) =>
        {
            keyType = e.HostKeyName;
            key = e.HostKey;
            e.CanTrust = true;
        };
        await client.ConnectAsync(CancellationToken.None);
        client.Disconnect();
        return (keyType!, Convert.ToBase64String(key!));
    }

    private static async Task WithKnownHostsAsync(string line, Func<string, Task> test)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cl-known-hosts-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(path, "# test known_hosts\n" + line + "\n");
        try { await test(path); }
        finally { File.Delete(path); }
    }

}
