# Seed The PostgreSQL Standby On 10.10.10.58

Primary host: `10.10.10.9`
Standby host: `10.10.10.58`

This runbook gives the exact Windows PowerShell command sequence to turn `10.10.10.58` into a streaming standby for ShopInventory.

Critical prerequisite: physical standby replication requires the same PostgreSQL major version on both nodes.

Current environment:

- primary `10.10.10.9` = PostgreSQL `17`
- standby candidate `10.10.10.58` = PostgreSQL `18`

That means this runbook must not be executed yet against the current hosts as they stand. First align the major versions.

Recommended path for the current environment:

1. Reinstall or replace PostgreSQL `18` on `10.10.10.58` with PostgreSQL `17`.
2. Reapply the standby `postgresql.conf` and `pg_hba.conf` templates.
3. Run this seeding sequence.

Alternative path:

1. Upgrade the live primary `10.10.10.9` from `17` to `18` in a planned outage.
2. Confirm the application is stable on `18`.
3. Rerun this standby seeding sequence using `18` on both hosts.

If you choose that route, use this runbook first:

- [docs/operations/postgresql-primary-upgrade-10.10.10.9-17-to-18.md](docs/operations/postgresql-primary-upgrade-10.10.10.9-17-to-18.md)

Version assumption used here after alignment: both nodes are running the same PostgreSQL major version and the standby host uses the matching Windows install layout.

Example layout once aligned:

- `C:\Program Files\PostgreSQL\16\bin`
- `C:\Program Files\PostgreSQL\16\data`
- Windows service name `postgresql-x64-16`

If your installed PostgreSQL major version is not `16`, change the `$PgVersion` variable before running the commands.

## 1. Prepare The Standby Host

Run the following in an elevated PowerShell session on `10.10.10.58`:

```powershell
$PgVersion = '16'
$PrimaryHost = '10.10.10.9'
$StandbyHost = '10.10.10.58'
$ReplicationUser = 'repl_user'
$ReplicationSlot = 'shopinventory_standby'
$PgRoot = "C:\Program Files\PostgreSQL\$PgVersion"
$PgBin = Join-Path $PgRoot 'bin'
$PgData = Join-Path $PgRoot 'data'
$PgService = "postgresql-x64-$PgVersion"
$ArchivePath = 'D:\PostgreSQL\Archive'
$BackupStamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$OldDataPath = "$PgData.pre-ha-$BackupStamp"

New-Item -ItemType Directory -Path $ArchivePath -Force | Out-Null
```

## 2. Stop PostgreSQL On The Standby

```powershell
Stop-Service -Name $PgService -Force
Get-Service -Name $PgService
```

Expected state after the second command: `Stopped`.

## 3. Preserve The Current Data Directory

This keeps a rollback copy of the old standalone data directory instead of deleting it immediately.

```powershell
if (Test-Path $PgData) {
    Rename-Item -Path $PgData -NewName (Split-Path $OldDataPath -Leaf)
}

Test-Path $OldDataPath
```

Expected result from the last command: `True`.

## 4. Run pg_basebackup From 10.10.10.58 Against 10.10.10.9

Replace the password placeholder first.

```powershell
$env:PGPASSWORD = 'REPLACE_WITH_REPLICATION_PASSWORD'

& "$PgBin\pg_basebackup.exe" `
  -h $PrimaryHost `
  -p 5432 `
  -U $ReplicationUser `
  -D $PgData `
  -R `
  -X stream `
  -C `
  -S $ReplicationSlot `
  -P

Remove-Item Env:PGPASSWORD
```

What this does:

- pulls a fresh base backup from `10.10.10.9`
- creates replication slot `shopinventory_standby` on the primary if it does not already exist
- creates `standby.signal`
- writes `primary_conninfo` and `primary_slot_name` into `postgresql.auto.conf`

Because `-R` already writes the recovery settings, keep `postgresql.auto.conf` as the source of truth for `primary_conninfo` and `primary_slot_name`.

## 5. Apply The Standby Runtime Configuration

After the base backup completes, merge the operational settings from:

- [docs/operations/postgresql-standby-10.10.10.58.conf](docs/operations/postgresql-standby-10.10.10.58.conf)

into the real standby `postgresql.conf` under:

- `C:\Program Files\PostgreSQL\16\data\postgresql.conf`

Important:

- keep `standby.signal` in the data directory
- keep `primary_conninfo` and `primary_slot_name` in `postgresql.auto.conf`
- if you copy settings from the standby template, do not duplicate those two settings in `postgresql.conf`

## 6. Confirm Recovery Files Exist

```powershell
Test-Path "$PgData\standby.signal"
Get-Content "$PgData\postgresql.auto.conf"
```

You should see:

- `True` for `standby.signal`
- `primary_conninfo = 'host=10.10.10.9 ...'`
- `primary_slot_name = 'shopinventory_standby'`

## 7. Start PostgreSQL On The Standby

```powershell
Start-Service -Name $PgService
Get-Service -Name $PgService
```

Expected state after the second command: `Running`.

## 8. Validate Replication

In pgAdmin on the primary `10.10.10.9`, run:

```sql
SELECT application_name, client_addr, state, sync_state
FROM pg_stat_replication;
```

Expected result:

- one row with `application_name = 'shopinventory_standby'`
- `client_addr = 10.10.10.58`
- `state = streaming`

In pgAdmin on the standby `10.10.10.58`, run:

```sql
SELECT pg_is_in_recovery();
```

Expected result:

```sql
pg_is_in_recovery
-----------------
true
```

## 9. Optional Rollback If Seeding Fails

If PostgreSQL does not start cleanly on `10.10.10.58`:

```powershell
Stop-Service -Name $PgService -Force -ErrorAction SilentlyContinue

if (Test-Path $PgData) {
    Remove-Item -Path $PgData -Recurse -Force
}

if (Test-Path $OldDataPath) {
    Rename-Item -Path $OldDataPath -NewName 'data'
}

Start-Service -Name $PgService
```

Use that only to restore the previous local standalone state while you investigate the replication error.