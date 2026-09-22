# Storage.Integration.Tests

Live tests for every CL.Storage backend. FTP, SFTP, and WebDAV run against real servers; S3, Azure
Blob, Google Cloud Storage, and Swift run against local emulators (MinIO, Azurite, fake-gcs-server,
Swift SAIO), so no cloud account is needed. Each provider's tests skip
themselves unless its `CL_STORAGE_TEST_*` variables are set, so the project is safe to run anywhere.

```sh
docker compose -f tests/Storage.Integration.Tests/docker-compose.yml up -d --build

export CL_STORAGE_TEST_SFTP_HOST=127.0.0.1 CL_STORAGE_TEST_SFTP_PORT=2022 \
       CL_STORAGE_TEST_SFTP_USER=cltest CL_STORAGE_TEST_SFTP_PASS=cltest-pw
export CL_STORAGE_TEST_FTP_HOST=127.0.0.1 CL_STORAGE_TEST_FTP_PORT=2021 \
       CL_STORAGE_TEST_FTP_USER=cltest CL_STORAGE_TEST_FTP_PASS=cltest-pw CL_STORAGE_TEST_FTP_ROOT=home/cltest
export CL_STORAGE_TEST_WEBDAV_URL=http://127.0.0.1:8080/ \
       CL_STORAGE_TEST_WEBDAV_USER=cltest CL_STORAGE_TEST_WEBDAV_PASS=cltest-pw

export CL_STORAGE_TEST_S3_URL=http://127.0.0.1:9010        CL_STORAGE_TEST_S3_ACCESSKEY=cltest CL_STORAGE_TEST_S3_SECRETKEY=cltest-pw-minio
export CL_STORAGE_TEST_AZURE_CONNECTION_STRING='UseDevelopmentStorage=true'
export CL_STORAGE_TEST_GCS_URL=http://127.0.0.1:4443
export CL_STORAGE_TEST_SWIFT_AUTH_URL=http://127.0.0.1:8082/auth/v1.0        CL_STORAGE_TEST_SWIFT_USER=test:tester CL_STORAGE_TEST_SWIFT_KEY=testing
export CL_STORAGE_TEST_PROXY_HOST=127.0.0.1   # HTTP 3128, SOCKS5 1080
export CL_STORAGE_TEST_FTPS_HOST=127.0.0.1 CL_STORAGE_TEST_FTPS_PORT=2024        CL_STORAGE_TEST_FTPS_USER=cltest CL_STORAGE_TEST_FTPS_PASS=cltest-pw

dotnet test tests/Storage.Integration.Tests -c Release -p:CodeLogicFromNuGet=true
```

Optional: `CL_STORAGE_TEST_SFTP_ROOT` (default `upload`) and `CL_STORAGE_TEST_FTP_ENCRYPTION`
(`None`, `Explicit`, or `Implicit`; default `None`).

Buckets and containers (`cl-test` by default) are created by the tests. Emulators differ from the
real services in a few places — for example Azurite answers a bad account key with
`AuthorizationFailure` where Azure sends `AuthenticationFailed` — and those tests accept both.

The SFTP tests also use `fixtures/` (throwaway key pairs mounted into the SFTP container) and a
`jump` bastion on port 2023 for jump-host tests (`CL_STORAGE_TEST_SSH_JUMP_HOST`/`_PORT` override it).
The suite runs sequentially: several tests disable retries to observe a single failure, and parallel
SSH handshakes would trip the server's `MaxStartups` limit.
