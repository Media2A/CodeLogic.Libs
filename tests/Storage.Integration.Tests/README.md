# Storage.Integration.Tests

Live tests for the CL.Storage FTP, SFTP, and WebDAV backends. Each provider's tests skip
themselves unless its `CL_STORAGE_TEST_*` variables are set, so the project is safe to run anywhere.

```sh
docker compose -f tests/Storage.Integration.Tests/docker-compose.yml up -d

export CL_STORAGE_TEST_SFTP_HOST=127.0.0.1 CL_STORAGE_TEST_SFTP_PORT=2022 \
       CL_STORAGE_TEST_SFTP_USER=cltest CL_STORAGE_TEST_SFTP_PASS=cltest-pw
export CL_STORAGE_TEST_FTP_HOST=127.0.0.1 CL_STORAGE_TEST_FTP_PORT=2021 \
       CL_STORAGE_TEST_FTP_USER=cltest CL_STORAGE_TEST_FTP_PASS=cltest-pw CL_STORAGE_TEST_FTP_ROOT=home/cltest
export CL_STORAGE_TEST_WEBDAV_URL=http://127.0.0.1:8080/ \
       CL_STORAGE_TEST_WEBDAV_USER=cltest CL_STORAGE_TEST_WEBDAV_PASS=cltest-pw

dotnet test tests/Storage.Integration.Tests -c Release -p:CodeLogicFromNuGet=true
```

Optional: `CL_STORAGE_TEST_SFTP_ROOT` (default `upload`) and `CL_STORAGE_TEST_FTP_ENCRYPTION`
(`None`, `Explicit`, or `Implicit`; default `None`).
