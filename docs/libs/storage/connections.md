# Connections

> How to connect to each kind of server: authentication, trust, proxies, session pools and retries,
> runtime connection changes, and checking a connection before and after you save it.

See the [overview](index.md) for the configuration sections and the mount model.

## SFTP: authentication, host keys, and jump hosts

```json
{
  "Host": "sftp.internal",
  "Username": "deploy",
  "AuthenticationMode": "Auto",
  "Password": "...",
  "PrivateKeyPath": "/secrets/id_ed25519",
  "PrivateKeyContent": null,
  "AdditionalPrivateKeyPaths": [],
  "PrivateKeyPassphrase": "...",
  "KnownHostsPath": "/home/app/.ssh/known_hosts",
  "HostKeyFingerprints": [],
  "Ciphers": ["aes256-gcm@openssh.com", "aes256-ctr"],
  "Encoding": "utf-8",
  "BufferSize": 262144,
  "JumpHost": {
    "Host": "bastion.example.com",
    "Username": "jump",
    "PrivateKeyPath": "/secrets/bastion_ed25519",
    "KnownHostsPath": "/home/app/.ssh/known_hosts"
  }
}
```

- **`AuthenticationMode`**: `Password`, `PrivateKey`, `KeyboardInteractive` (answers the password
  prompt), or `Auto`, which offers keys, then password, then keyboard-interactive, and also satisfies
  servers that demand several methods. Keys can be files or inline text (`PrivateKeyContent`) from a
  secret store. SSH agents are not supported by the underlying SSH library.
- **Host keys** are trusted through `HostKeyFingerprints` (`SHA256:...`), an OpenSSH `KnownHostsPath`
  (plain, hashed, wildcard, and `[host]:port` entries), or `AutoAcceptHostKey` for development. A key
  marked `@revoked` in `known_hosts` is refused even with auto-accept. A rejected key reports
  `storage.host_key_rejected` with the presented fingerprint in `Details`.
- **Algorithms**: `KeyExchangeAlgorithms`, `Ciphers`, `MacAlgorithms`, and `HostKeyAlgorithms`
  restrict and order what is offered, for hardening or for old servers. Unknown names fail validation
  and list what is supported.
- **`JumpHost`** tunnels through an SSH bastion. The target's key is still verified against the
  target's settings, and a configured `Proxy` applies to the bastion connection.

## FTP and FTPS

```json
{
  "Host": "ftp.partner.example",
  "EncryptionMode": "Explicit",
  "TrustedPublicKeySha256": ["SHA256:..."],
  "TlsProtocols": ["Tls12", "Tls13"],
  "EncryptDataChannel": true,
  "DataConnectionMode": "AutoPassive",
  "ActivePortMin": 50000,
  "ActivePortMax": 50100,
  "ActiveExternalIp": "203.0.113.7",
  "Encoding": "windows-1252",
  "TransferType": "Binary",
  "ListingParser": "Auto",
  "ServerTimeZone": "Europe/Copenhagen",
  "ReadTimeoutSeconds": 60,
  "SocketKeepAlive": true,
  "LoginCommands": ["SITE UMASK 022"]
}
```

- **Pins**: `TrustedCertificateSha256` pins the whole certificate; `TrustedPublicKeySha256` pins only
  its public key, so it keeps working across renewals that keep the key. Pinned self-signed
  certificates are accepted unless `RequireValidCertificateChain` is set. Without pins, normal
  validation applies. TLS problems report `storage.tls_failure`.
- **Active mode** behind NAT: `ActivePortMin`/`ActivePortMax` and `ActiveExternalIp`.
- **Encodings** such as `windows-1252`, `iso-8859-1`, `ibm437`, and `shift_jis` are supported for file
  names on older servers.
- **Listings**: `ListingParser` forces a format (`Unix`, `Windows`, `Machine`, …) and `ServerTimeZone`
  converts times from servers that report local time.
- **Timeouts**: `ConnectTimeoutSeconds`, `ReadTimeoutSeconds`, and `DataConnectionTimeoutSeconds`
  fall back to `TimeoutSeconds`.
- **`LoginCommands`** run after every login; a command the server rejects fails the connection so
  misconfiguration surfaces immediately.

## WebDAV

`AuthenticationMode` accepts `None`, `Basic` (sent up front, saving a challenge round trip),
`BearerToken`, `Digest`, `Ntlm`, `Negotiate`, and `Windows` (current user). HTTPS endpoints support
`TrustedCertificateSha256` and `TrustedPublicKeySha256` pins, `RequireValidCertificateChain`, and a
PFX `ClientCertificatePath` for mutual TLS. `MaxConnectionsPerServer` caps concurrent connections.

## Cloud emulators and compatible services

- **S3-compatible** (MinIO, Ceph, …): set `ServiceUrl` and usually `ForcePathStyle`.
- **Azure Blob**: a connection string works with Azurite (`UseDevelopmentStorage=true`).
- **Google Cloud Storage**: `ServiceUrl` plus `AuthenticationMode = Anonymous` targets an emulator such as fake-gcs-server.
- **Swift**: Keystone, or `AuthenticationMode = TempAuthV1` for SAIO-style servers (`AuthenticationUrl` ending in `/auth/v1.0`).

Plain-HTTP endpoints need `AllowInsecureHttp = true`.

## Proxies

Every remote provider can tunnel through an HTTP (`CONNECT`), SOCKS5, or SOCKS4 proxy:

```json
"Proxy": { "Type": "Socks5", "Host": "proxy.corp.local", "Port": 1080, "Username": "me", "Password": "..." }
```

SOCKS5 and HTTP proxies resolve the destination host name on the proxy side. SOCKS4 cannot, so
the host must resolve from the client, and SOCKS4 carries no password. FTP data connections are
tunnelled as well, so use passive mode, and the server's passive address must be reachable
from the proxy.

## Sessions, retries, and keep-alive

FTP and SFTP keep authenticated sessions in a per-connection pool, and FTP, SFTP, and WebDAV
retry transient failures automatically. Both are tuned per connection:

```json
{
  "Session": {
    "MaxSessions": 4,
    "MaxIdleSessions": 2,
    "IdleLifetimeSeconds": 120,
    "AcquireTimeoutSeconds": 30,
    "ValidateAfterIdleSeconds": 15,
    "KeepAliveSeconds": 60
  },
  "Retry": {
    "RetryCount": 3,
    "BaseDelayMs": 100,
    "MaxDelayMs": 30000,
    "RetryNonIdempotent": false
  }
}
```

- `MaxSessions` caps open sessions, busy or idle, so the library stays under a server's per-user
  connection limit. Callers beyond it wait up to `AcquireTimeoutSeconds`, then receive
  `storage.server_busy`.
- A pooled session idle longer than `ValidateAfterIdleSeconds` is probed (FTP `NOOP`, SFTP `stat`)
  before reuse. Sessions that time out, drop, or fail TLS mid-operation are closed instead of reused.
- `KeepAliveSeconds` sends FTP `NOOP` or SSH keep-alive packets while a session is open.
- Reads, listings, info, and directory creation retry on timeouts, refused or dropped connections,
  and busy servers, with exponential backoff and jitter; a server `Retry-After` is honored. Uploads
  retry only from a seekable stream, which is replayed from its starting position; staged uploads
  never leave a partial file. Deletes and moves retry only with `RetryNonIdempotent`, because the
  first attempt may already have succeeded.
- Set `RetryCount` to `0` for single-attempt behavior.

## Runtime connections and native clients

```csharp
await storage.AddOrUpdateConnectionAsync("backup", new SftpConnectionConfig
{
    Host = "sftp.example.com",
    Username = "backup",
    AuthenticationMode = SftpAuthenticationMode.PrivateKey,
    PrivateKeyPath = @"C:\keys\backup_ed25519",
    HostKeyFingerprints = ["SHA256:..."]
});

Result<HealthStatus> health = await storage.CheckConnectionHealthAsync("backup");
```

Runtime changes are persisted to the provider's JSON section, or installed for the process only with
`persist: false`. A replacement is health-checked before it goes live. `RegisterBackend` installs a
custom `IStorageBackend`.

Reusable native SDK clients and scoped session clients remain available as an escape hatch for
provider administration (bucket/container creation, IAM/lifecycle policies) and non-portable options:

```csharp
IAmazonS3 s3 = storage.GetNativeClient<IAmazonS3>("media");

var opened = await storage.OpenNativeConnectionAsync<AsyncFtpClient>("legacy-ftp");
if (opened.IsSuccess)
{
    await using var lease = opened.Value!;
    AsyncFtpClient ftp = lease.Client;
}
```

Do not dispose reusable clients returned by `GetNativeClient`; dispose session leases.

## Testing settings before saving them

`TestConnectionAsync` tries settings without saving or registering them, even before the library is
initialized. It reports each step it ran — `validate`, `connect`, `list`, `details` — and stops at the
first failure:

```csharp
var settings = new SftpConnectionConfig { Host = "sftp.example.com", Username = "deploy", Password = "..." };
var report = await storage.TestConnectionAsync(settings);

foreach (var step in report.Steps)
    Console.WriteLine($"{step.Name,-8} {(step.Succeeded ? "ok" : step.Error!.Code)} {step.Duration.TotalMilliseconds:0} ms");
```

When the server's certificate or host key is rejected, `ServerIdentity` still says what was presented,
which is what a setup screen needs for "the server presented this fingerprint — trust it?":

```csharp
if (!report.Succeeded && report.ServerIdentity is { Kind: "ssh-host-key" } key)
{
    // Show key.Fingerprint and key.Algorithm, and on confirmation:
    settings.HostKeyFingerprints = [key.Fingerprint];
}

if (!report.Succeeded && report.ServerIdentity is { Kind: "tls-certificate" } certificate)
{
    // certificate.Subject, certificate.Issuer, certificate.NotAfter describe it.
    // Pin the public key so the pin survives a renewal that keeps the key:
    ftpSettings.TrustedPublicKeySha256 = [certificate.PublicKeyFingerprint!];
}
```

## Diagnosing a live connection

```csharp
var diagnostics = (await storage.GetConnectionDiagnosticsAsync("partner")).Value!;
Console.WriteLine($"{diagnostics.Host}:{diagnostics.Port} over {diagnostics.Security}");
Console.WriteLine($"{diagnostics.ServerSoftware} ({diagnostics.ServerSystem})");
```

| Property | FTP/FTPS | SFTP | WebDAV |
|---|---|---|---|
| `Security` | `None` or `Tls` | `Ssh` | `None` or `Tls` |
| `ServerSystem` | `SYST` reply | SSH version string | `Server` header |
| `ServerSoftware` | detected server type | e.g. `OpenSSH_9.6p1` | first `Server` token |
| `ServerFeatures` | `FEAT` capabilities | — | `DAV` classes and `Allow` methods |
| `Negotiated` | `tls`, `cipher` | `kex`, `hostKey`, `cipher`, `mac`, `sftp` | `http` version |
| `ServerIdentity` | certificate | host key | certificate |
| `Pool` | session counters | session counters | — |

Other providers report host, port, security, and `LastHealth`. `GetConnections()` also carries
`Host`, `Port`, `Security`, and `LastHealth` for every configured connection. Diagnostics never contain
credentials.
