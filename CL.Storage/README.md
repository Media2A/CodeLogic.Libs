# CodeLogic.Storage

Provider-neutral storage contracts and providers for CodeLogic applications.

This initial package surface includes the common storage API, configuration models,
path safety rules, and the local/mounted-filesystem provider. Additional providers
are added behind the same contract without exposing their SDK types through common
models or interfaces.

`StorageLibrary` participates directly in the CodeLogic four-phase lifecycle and
returns stable `IStorageService` proxies for named connections. Local connections
can be added, replaced, or removed at runtime and optionally persisted to
`config.storage.local.json`; prebuilt custom backends are runtime-only.

Reusable native clients returned by `GetNativeClient<TClient>` remain owned by the
library and must not be disposed by callers. A raw client already handed out becomes
invalid if its connection is replaced or removed. Session-oriented providers use
`OpenNativeConnectionAsync<TClient>` instead; disposing its lease releases both the
native session and the registry operation lease.

Storage backend health checks are expected to honor their cancellation token
promptly. The library hard-bounds the caller-facing health wait, but it retains an
unpublished backend or published backend lease until the provider's actual health
task settles. Consequently, replacement, removal, or shutdown can keep waiting for
a backend that ignores health-check cancellation even after the health caller has
received a timeout or cancellation result.
