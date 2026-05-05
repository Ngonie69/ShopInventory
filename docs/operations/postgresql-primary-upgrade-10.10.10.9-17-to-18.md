# Upgrade The Primary On 10.10.10.9 From PostgreSQL 17 To 18

Primary host: `10.10.10.9`
Standby host: `10.10.10.58`

Use this runbook only if you intentionally choose the higher-risk path of upgrading the live primary first so it matches the existing PostgreSQL `18` installation on `10.10.10.58`.

## What This Path Does

- upgrades the live primary on `10.10.10.9` from PostgreSQL `17` to `18`
- keeps the old PostgreSQL `17` cluster available for rollback by using `pg_upgrade` copy mode
- leaves `10.10.10.58` untouched until the primary is successfully on `18`
- allows you to seed `10.10.10.58` as a real standby only after the primary upgrade is complete

## Risk Profile

This path is higher risk than reinstalling `10.10.10.58` to `17` because it changes the live production primary before you have a working standby.

Use it only when:

- you accept a planned outage window
- you have verified backups first
- PostgreSQL `18` is installed on `10.10.10.9` before cutover
- you are prepared to roll back to the untouched PostgreSQL `17` cluster if validation fails

## Assumptions

Old primary version on `10.10.10.9`:

- PostgreSQL `17`
- install root `C:\Program Files\PostgreSQL\17`
- service name `postgresql-x64-17`

New version to be installed on `10.10.10.9`:

- PostgreSQL `18`
- install root `C:\Program Files\PostgreSQL\18`
- service name `postgresql-x64-18`

If your real service names or install paths differ, change the variables before running the commands.

## 1. Install PostgreSQL 18 On 10.10.10.9 First

Before the outage window, install PostgreSQL `18` side-by-side on `10.10.10.9`.

Installer guidance:

- install into `C:\Program Files\PostgreSQL\18`
- let it create a fresh empty cluster under `C:\Program Files\PostgreSQL\18\data`
- if the installer requires a different port during side-by-side setup, use a temporary one such as `5433`
- stop the `postgresql-x64-18` service after installation completes

Do not point ShopInventory to PostgreSQL `18` yet.

## 2. Prepare Variables On 10.10.10.9

Run this in an elevated PowerShell session on `10.10.10.9`:

```powershell
$OldPgVersion = '17'
$NewPgVersion = '18'
$PrimaryHost = '10.10.10.9'
$StandbyHost = '10.10.10.58'
$OldPgRoot = "C:\Program Files\PostgreSQL\$OldPgVersion"
$NewPgRoot = "C:\Program Files\PostgreSQL\$NewPgVersion"
$OldPgBin = Join-Path $OldPgRoot 'bin'
$NewPgBin = Join-Path $NewPgRoot 'bin'
$OldPgData = Join-Path $OldPgRoot 'data'
$NewPgData = Join-Path $NewPgRoot 'data'
$OldPgService = "postgresql-x64-$OldPgVersion"
$NewPgService = "postgresql-x64-$NewPgVersion"
$UpgradeRoot = 'C:\PostgreSQLUpgrade\17-to-18'
$BackupRoot = 'D:\PostgreSQL\UpgradeBackups\17-to-18'

New-Item -ItemType Directory -Path $UpgradeRoot -Force | Out-Null
New-Item -ItemType Directory -Path $BackupRoot -Force | Out-Null
```

## 3. Capture Verified Backups Before Touching The Cluster

If your current `postgres` password is `root`, use that value when setting `PGPASSWORD` below.

```powershell
$env:PGPASSWORD = 'REPLACE_WITH_POSTGRES_PASSWORD'

& "$OldPgBin\pg_dumpall.exe" --globals-only -U postgres -h 127.0.0.1 -p 5432 -f "$BackupRoot\globals.sql"
& "$OldPgBin\pg_dump.exe" -Fc -U postgres -h 127.0.0.1 -p 5432 -d shopinventory -f "$BackupRoot\shopinventory.backup"
& "$OldPgBin\pg_dump.exe" -Fc -U postgres -h 127.0.0.1 -p 5432 -d shopinventoryweb -f "$BackupRoot\shopinventoryweb.backup"

Remove-Item Env:PGPASSWORD

Copy-Item "$OldPgData\postgresql.conf" "$BackupRoot\postgresql-17.conf"
Copy-Item "$OldPgData\pg_hba.conf" "$BackupRoot\pg_hba-17.conf"
```

Confirm the backup files exist before proceeding.

## 4. Stop ShopInventory Traffic During The Outage Window

If the ShopInventory IIS app pools run on `10.10.10.9`, stop them before stopping PostgreSQL:

```powershell
Import-Module WebAdministration
Stop-WebAppPool -Name 'ShopInventoryAPI'
Stop-WebAppPool -Name 'ShopInventoryWeb'
```

## 5. Stop Both PostgreSQL Services

```powershell
Stop-Service -Name $OldPgService -Force
Stop-Service -Name $NewPgService -Force -ErrorAction SilentlyContinue

Get-Service -Name $OldPgService, $NewPgService
```

Expected state for both services: `Stopped`.

## 6. Confirm The New PostgreSQL 18 Cluster Exists

The side-by-side PostgreSQL `18` install should already have created an empty data directory.

```powershell
Test-Path "$NewPgData\PG_VERSION"
```

Expected result: `True`.

If that returns `False`, do not continue. Complete the PostgreSQL `18` installation first.

## 7. Run pg_upgrade Precheck In Copy Mode

Run from a writable working directory because `pg_upgrade` generates scripts there.

```powershell
Set-Location $UpgradeRoot
$env:PGPASSWORD = 'REPLACE_WITH_POSTGRES_PASSWORD'

& "$NewPgBin\pg_upgrade.exe" `
  --check `
  --old-bindir "$OldPgBin" `
  --new-bindir "$NewPgBin" `
  --old-datadir "$OldPgData" `
  --new-datadir "$NewPgData" `
  --username postgres `
  --jobs 4

Remove-Item Env:PGPASSWORD
```

Do not use `--link` on the first production upgrade. Copy mode preserves the old PostgreSQL `17` cluster for rollback.

## 8. Run The Actual Upgrade

```powershell
Set-Location $UpgradeRoot
$env:PGPASSWORD = 'REPLACE_WITH_POSTGRES_PASSWORD'

& "$NewPgBin\pg_upgrade.exe" `
  --old-bindir "$OldPgBin" `
  --new-bindir "$NewPgBin" `
  --old-datadir "$OldPgData" `
  --new-datadir "$NewPgData" `
  --username postgres `
  --jobs 4

Remove-Item Env:PGPASSWORD
```

This should generate helper scripts in `$UpgradeRoot`, including:

- `analyze_new_cluster.bat`
- `delete_old_cluster.bat`

Do not run `delete_old_cluster.bat` until the new PostgreSQL `18` primary is fully validated and the rollback window is closed.

## 9. Reapply The Production PostgreSQL 18 Runtime Configuration

After `pg_upgrade` completes, apply the primary configuration to the PostgreSQL `18` cluster on `10.10.10.9`.

Use these documents as the source of truth:

- [docs/operations/postgresql-primary-10.10.10.9.conf](docs/operations/postgresql-primary-10.10.10.9.conf)
- [docs/operations/pg_hba-primary-10.10.10.9.conf](docs/operations/pg_hba-primary-10.10.10.9.conf)

Apply them into the real PostgreSQL `18` files under:

- `C:\Program Files\PostgreSQL\18\data\postgresql.conf`
- `C:\Program Files\PostgreSQL\18\data\pg_hba.conf`

Important:

- ensure the PostgreSQL `18` cluster will listen on production port `5432`
- keep the archive and replication settings aligned with the primary template
- keep the `pg_hba.conf` rules aligned with the primary authentication template
- leave the PostgreSQL `17` service stopped during cutover validation

## 10. Start PostgreSQL 18 And Validate The Cluster

```powershell
Start-Service -Name $NewPgService
Get-Service -Name $NewPgService
```

Expected state: `Running`.

Then validate basic connectivity:

```powershell
$env:PGPASSWORD = 'REPLACE_WITH_POSTGRES_PASSWORD'

& "$NewPgBin\psql.exe" -U postgres -h 127.0.0.1 -p 5432 -d postgres -c "SELECT version();"
& "$NewPgBin\psql.exe" -U postgres -h 127.0.0.1 -p 5432 -d shopinventory -c "SELECT current_database();"
& "$NewPgBin\psql.exe" -U postgres -h 127.0.0.1 -p 5432 -d shopinventoryweb -c "SELECT current_database();"

Remove-Item Env:PGPASSWORD
```

Expected result: PostgreSQL `18` is reported and both application databases open successfully.

## 11. Run Post-Upgrade Statistics Refresh

Run the generated script from the upgrade working directory:

```powershell
Set-Location $UpgradeRoot
& ".\analyze_new_cluster.bat"
```

## 12. Bring The Application Back And Check Health

If the IIS app pools were stopped on `10.10.10.9`, bring them back:

```powershell
Import-Module WebAdministration
Start-WebAppPool -Name 'ShopInventoryAPI'
Start-WebAppPool -Name 'ShopInventoryWeb'
```

Then validate the application-level checks you use operationally, including API readiness and any critical login or invoice smoke tests.

## 13. Seed 10.10.10.58 As The Real Standby After The Upgrade

Once `10.10.10.9` is confirmed healthy on PostgreSQL `18`, seed `10.10.10.58` using:

- [docs/operations/postgresql-standby-seeding-10.10.10.58.md](docs/operations/postgresql-standby-seeding-10.10.10.58.md)

For that runbook, set:

```powershell
$PgVersion = '18'
```

After seeding, confirm:

- `SELECT version();` on `10.10.10.9` shows PostgreSQL `18`
- `SELECT pg_is_in_recovery();` on `10.10.10.58` returns `true`
- `pg_stat_replication` on `10.10.10.9` shows `10.10.10.58` in `streaming`

## 14. Rollback If Validation Fails Before The Standby Is Built

Because this runbook uses `pg_upgrade` copy mode, the original PostgreSQL `17` cluster is still available.

Rollback path:

```powershell
Stop-Service -Name $NewPgService -Force -ErrorAction SilentlyContinue
Start-Service -Name $OldPgService

Import-Module WebAdministration
Start-WebAppPool -Name 'ShopInventoryAPI'
Start-WebAppPool -Name 'ShopInventoryWeb'
```

Use that only after restoring the PostgreSQL `17` port/config ownership on `10.10.10.9` if PostgreSQL `18` was already bound to `5432`.

Do not delete the old PostgreSQL `17` cluster until:

- the primary is stable on PostgreSQL `18`
- the standby on `10.10.10.58` is seeded and streaming
- the ShopInventory app passes your production smoke checks