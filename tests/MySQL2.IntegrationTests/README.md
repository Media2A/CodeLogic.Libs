# MySQL2.IntegrationTests

A simple console test suite that boots the **real CodeLogic runtime** and exercises
CL.MySQL2 against a local MySQL/MariaDB. Prints PASS/FAIL per check and exits
non-zero on any failure. Not part of the CI release set (the release workflow
builds a hardcoded list of `CL.*` libraries only).

## Prerequisites

A MySQL-compatible server on `127.0.0.1:3310`, user `root`, no password, with a
`cl_test` database. A no-admin MariaDB works well:

```powershell
# Extract MariaDB without installing a service (no elevation needed)
$msi = (winget download --id MariaDB.Server ...)   # or use the cached winget MSI
msiexec /a "<mariadb>.msi" /qn TARGETDIR="$env:LOCALAPPDATA\clmaria"
& "$env:LOCALAPPDATA\clmaria\MariaDB 12.3\bin\mariadb-install-db.exe" --datadir="$env:LOCALAPPDATA\clmaria\data"
& "$env:LOCALAPPDATA\clmaria\MariaDB 12.3\bin\mariadbd.exe" --no-defaults --datadir="$env:LOCALAPPDATA\clmaria\data" --port=3310 --bind-address=127.0.0.1 --console
# then: mariadb -h127.0.0.1 -P3310 -uroot -e "CREATE DATABASE cl_test"
```

Adjust host/port in `Program.cs` (the `config.mysql.json` it writes) if different.

## Run

```bash
dotnet run -c Debug
```

## Covers

- Typed JOINs (inner + left, projection, ordering)
- Subquery filters (`WhereExists`/`WhereNotExists`/`WhereIn`/`WhereNotIn`)
- Column rename via `[Column(PreviousName=...)]` (data preserved)
- Result cache + multi-node `ICacheCoordinator` fan-out
