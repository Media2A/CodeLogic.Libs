# Errors & Events

> The stable `storage.*` error codes every provider maps to, helpers for deciding whether to retry,
> and the events the library publishes for monitoring.

## Error codes

Expected failures come back as a failed `Result` with a stable code. Caller cancellation propagates as
`OperationCanceledException` from connection calls (uploads, downloads, listings, and
`IStorageService.CopyAsync`/`MoveAsync`) and from sync planning. It is reported instead where a report
says what was left: `StorageLibrary.CopyAsync`/`MoveAsync` (`Outcome = Cancelled`), the transfer queue's
`Result` methods, and a sync cancelled while applying (a successful result with `report.Cancelled = true`).
Every provider maps its own failures to the same codes:

| Code | Meaning | Typical sources |
|---|---|---|
| `storage.not_found` | the item does not exist | FTP 550, SFTP no-such-file, HTTP 404 |
| `storage.conflict` | the destination exists, or a condition failed | `ConflictPolicy.Fail`, ETag mismatch |
| `storage.invalid_path` | the path escapes the mount or is malformed | `..`, rooted paths, rejected names |
| `storage.invalid_content` | an argument or option is invalid | bad octal mode, bad command text |
| `storage.too_large` | a bounded read or upload exceeded its limit | `DownloadBytesAsync` limits |
| `storage.unsupported` | the connection cannot do this | missing capability, server lacks a command |
| `storage.authentication_failed` | the credentials were rejected | FTP 530, SSH auth, HTTP 401 |
| `storage.permission_denied` | signed in, but not allowed | FTP 550/553 permission, SFTP, HTTP 403, local ACL |
| `storage.tls_failure` | TLS handshake or certificate pin failed | untrusted or unpinned certificate |
| `storage.host_key_rejected` | the SSH host key is not trusted | no matching fingerprint or `known_hosts` entry |
| `storage.connection_failed` | could not connect | DNS failure, refused, unreachable, proxy failure |
| `storage.connection_lost` | the connection dropped mid-operation | FTP 421/426, reset sockets |
| `storage.server_busy` | rate limited or out of sessions | HTTP 429/503, FTP 421, session pool full |
| `storage.quota_exceeded` | out of space or quota | FTP 452/552, HTTP 507, disk full |
| `storage.timeout` | the operation timed out | read/connect timeouts; a queue pause, cancel, or remove that stopped waiting after `ControlTimeout` (the request still applies) |
| `storage.unavailable` | the service is unavailable | provider outages; a disposed transfer queue; a job whose store failed 8 times in a row |
| `storage.partial_failure` | a multi-step operation stopped halfway | restore or move-source deletion failed, a WebDAV `207 Multi-Status` |
| `storage.cancelled` | the caller cancelled, reported where a report says what was left | a cancelled `CopyAsync`/`MoveAsync` (`Outcome = Cancelled`) |
| `storage.provider_error` | anything not classified above | carries the exception type (never its message) |

Classify errors by `Code` (or `StorageErrorInfo.IsTransient`), not by the Core error kind:
`storage.cancelled` is never transient, although it is built as an availability error.

`StorageErrorInfo` (namespace `CL.Storage.Errors`) helps act on them:

```csharp
if (StorageErrorInfo.IsTransient(result.Error))              // timeout, unavailable, connection_*, server_busy
{
    if (StorageErrorInfo.TryGetRetryAfter(result.Error, out var wait))
        await Task.Delay(wait);
}

if (StorageErrorInfo.TryGetDetail(result.Error, StorageErrorInfo.FtpReplyKey, out var reply))
    Console.WriteLine($"FTP server replied {reply}");         // also SftpStatusKey, HttpStatusKey
```

A failure raised after the destination was already written carries `destinationState=complete`
(`StorageErrorInfo.DestinationCommitted(error)`), with a `leftBehind=<path>` entry for each object the
provider could not remove, such as its own backup; treat the destination as committed and clean up, never
retry blindly. This applies to staged uploads and `StorageWriteStream.CommitAsync` too: a
`storage.partial_failure` with `destinationState=complete` means the content was written and the
`leftBehind` entries name what is still there. A WebDAV `207` carries `destinationState=partial`.

A rejected SSH host key carries `presentedFingerprint` in `Details`. To rebuild an error stored as its
code, message, and details (for example from a job store), use `StorageErrors.Create`. Provider response bodies,
credentials, and signed query strings are never included. FTP, SFTP, and WebDAV already retry
transient failures themselves (see [Connections](connections.md#sessions-retries-and-keep-alive)),
so a transient error you receive has already been retried.

### TLS failures

`storage.tls_failure` carries a `tlsReason` detail (`StorageErrorInfo.TlsReasonKey`) that says what to do:

| `tlsReason` | Meaning | Extra details |
|---|---|---|
| `server_certificate_rejected` | the server's certificate is not trusted or not pinned | `presentedCertificateSha256` and `presentedPublicKeySha256`, ready to pin |
| `client_certificate_rejected` | the server refused the client certificate: a credential problem | — |
| `protocol_mismatch` | no TLS version or cipher in common | — |
| `handshake_failed` | anything else during the handshake | — |
| `connection_interrupted` | a hint on `storage.connection_lost` (transient): the TLS stream failed after the handshake (OpenSSL unexpected EOF or bad record MAC, SChannel decrypt failure) | — |

The reason comes from the platform's own TLS stack: SChannel status codes on Windows (matched by code,
since Windows localizes the messages) and OpenSSL alerts on Linux. Only failures during the handshake are
TLS failures; a stream that breaks later is `storage.connection_lost`, which is transient.

A TLS 1.3 server refuses a client certificate only after the handshake, which the platform reports as a
dropped connection. It becomes `client_certificate_rejected` only when a connection that was asked for a
certificate (the server sent acceptable issuers or its certificate request) failed after our reply with
evidence of a refusal (an alert's worth of bytes, or a request sent on it that got no answer), and only
for the attempt that sent its request on that connection. A spare connection the pool opened and later
closed without using, or a refusal on a connection another transfer used, never turns this attempt's drop
into a refusal. A dropped connection on a server that merely asks for a certificate (optional client
certificates) stays `connection_lost`, with `tlsReason=client_certificate_rejected` as a hint. Behind an
HTTP proxy tunnel the TLS stream does not sit on the watched connection, so a refusal is never detected
there: it stays `connection_lost`, with at most that hint. FTP and
WebDAV add the presented certificate; S3, Azure, Google Cloud, and Swift report the reason only.

```csharp
if (StorageErrorInfo.TryGetDetail(result.Error, StorageErrorInfo.TlsReasonKey, out var reason)
    && reason == "server_certificate_rejected"
    && StorageErrorInfo.TryGetDetail(result.Error, StorageErrorInfo.PresentedPublicKeyKey, out var key))
{
    // Ask the user, then pin: settings.TrustedPublicKeySha256 = [key];
}
```

## Events

All events go to the CodeLogic event bus. Publishing never delays or fails the storage operation.

| Event | Published when |
|---|---|
| `StorageItemWrittenEvent` | an upload or append committed |
| `StorageItemDeletedEvent` | an item was deleted |
| `StorageItemCopiedEvent` / `StorageItemMovedEvent` | a copy or move within one connection completed |
| `StorageCrossConnectionCopyCompletedEvent` / `…MoveCompletedEvent` | a relayed transfer between connections completed (with counts) |
| `StorageDirectoryUploadedEvent` / `StorageDirectoryDownloadedEvent` | a local folder transfer completed |
| `StorageConnectionOpenedEvent` | an FTP or SFTP session opened and authenticated |
| `StorageConnectionLostEvent` | a session dropped, timed out, or failed TLS and was retired |
| `StorageConnectionRetryEvent` | a transient failure is being retried (attempt, delay, code) |
| `StorageConnectionHealthChangedEvent` | a health check found a connection in a new state (and on its first check) |
| `StorageOperationFailedEvent` | any service operation failed (operation, path, code) |
| `StorageTransferStartedEvent` / `CompletedEvent` / `FailedEvent` / `CancelledEvent` | a transfer-queue job started or finished |
| `StorageTransferRetryingEvent` | a queue job failed transiently and will run again after a delay |
| `StorageTransferBlockedEvent` | a queue job needs a person: an untrusted identity or rejected credentials |
| `StorageTransferNeedsReconciliationEvent` | a queue job left a mixed state that needs checking |
| `StorageTransferInterruptedEvent` | a queue job found running after a restart may have touched its destination |

```csharp
events.Subscribe<StorageConnectionHealthChangedEvent>(e =>
    logger.Warning($"{e.ConnectionId} is now {(e.Healthy ? "healthy" : $"unhealthy ({e.ErrorCode})")}"));

events.Subscribe<StorageOperationFailedEvent>(e =>
{
    if (e.ErrorCode != StorageErrors.NotFoundCode)
        logger.Warning($"{e.Operation} {e.Path} on {e.ConnectionId} failed: {e.ErrorCode}");
});
```

`StorageOperationFailedEvent` includes expected failures such as `storage.not_found`, so filter on
`ErrorCode`. Input rejected before reaching the provider (invalid paths or options) is not reported.
Each retry and each retired session is also logged as a warning.
