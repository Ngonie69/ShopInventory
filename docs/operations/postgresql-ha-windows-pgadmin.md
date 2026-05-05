# PostgreSQL HA On Windows With pgAdmin

## What pgAdmin Is And Is Not

pgAdmin is an administration client. It can connect to PostgreSQL servers, run queries, manage roles, inspect replication status, and help with backups or restores.

pgAdmin is **not** the PostgreSQL runtime, the replication engine, or the failover control plane.

For ShopInventory HA, you still need:

- a Windows server or VM running the PostgreSQL primary instance
- a second Windows server or VM running the PostgreSQL standby instance
- optional witness or cluster manager tooling if you want controlled automatic failover
- WAL archiving to durable storage

pgAdmin then connects to those servers so you can administer them.

Before you touch replication or failover settings, run the Windows readiness preflight from this repo:

```powershell
.\scripts\Test-PostgresWindowsHaReadiness.ps1 `
  -PrimaryHost 10.10.10.9 `
  -StandbyHost 10.10.10.58 `
  -IncludeDatabaseChecks
```

Use `-Credential` when both nodes accept the same Windows credential, or `-PrimaryCredential` and `-StandbyCredential` when they differ.

## Recommended Minimum Topology

Use separate Windows hosts:

- `db-primary`: active read-write PostgreSQL instance
- `db-standby`: hot standby receiving streaming replication
- `archive-share`: durable path for WAL archiving and restore drills

If you cannot support automatic failover safely yet, start with **manual promotion** of the standby. That is still a major improvement over a single localhost database.

## Step 1: Install PostgreSQL On Two Hosts

Install the same PostgreSQL major version on both Windows servers.

Current environment note:

- `10.10.10.9` is running PostgreSQL `17`
- `10.10.10.58` is running PostgreSQL `18`

That pairing cannot be used for physical streaming replication or `pg_basebackup` standby seeding. Do not try to seed `10.10.10.58` from `10.10.10.9` until the major versions match.

Safe options:

1. Reinstall `10.10.10.58` as PostgreSQL `17`, then seed it from the live primary `10.10.10.9`.
2. Plan a controlled upgrade of the live primary `10.10.10.9` to PostgreSQL `18`, then seed `10.10.10.58` afterward.

If you intentionally choose option `2`, use this exact runbook:

- [docs/operations/postgresql-primary-upgrade-10.10.10.9-17-to-18.md](docs/operations/postgresql-primary-upgrade-10.10.10.9-17-to-18.md)

For the current production state, option `1` is the lower-risk path because it avoids upgrading the live primary before you have a working standby.

Example host naming:

- `db-primary.internal`
- `db-standby.internal`

Keep:

- matching major versions
- matching locale and encoding choices
- the same extension set on both nodes

## Step 2: Configure The Primary

Edit `postgresql.conf` on the primary and set at least:

```conf
listen_addresses = '*'
port = 5432
wal_level = replica
max_wal_senders = 10
max_replication_slots = 10
hot_standby = on
archive_mode = on
archive_command = 'copy "%p" "\\archive-server\postgres-wal\%f"'
wal_keep_size = 4096MB
```

If low-latency synchronous replication is acceptable, also consider:

```conf
synchronous_commit = on
synchronous_standby_names = 'FIRST 1 (shopinventory_standby)'
```

If not, leave replication asynchronous and document the accepted RPO.

If you want full ready-to-edit templates instead of only the minimum settings, use:

- [docs/operations/postgresql-primary-10.10.10.9.conf](docs/operations/postgresql-primary-10.10.10.9.conf)
- [docs/operations/postgresql-standby-10.10.10.58.conf](docs/operations/postgresql-standby-10.10.10.58.conf)

## Step 3: Allow Replication And App Access

Update `pg_hba.conf` on both nodes so:

- the standby can start streaming from the primary immediately
- the promoted standby can accept ShopInventory traffic without an auth mismatch
- PostgreSQL administration remains limited to the specific hosts you intend to use

Use the full ready-to-edit templates:

- [docs/operations/pg_hba-primary-10.10.10.9.conf](docs/operations/pg_hba-primary-10.10.10.9.conf)
- [docs/operations/pg_hba-standby-10.10.10.58.conf](docs/operations/pg_hba-standby-10.10.10.58.conf)

These templates keep loopback access, allow `repl_user` between `10.10.10.9` and `10.10.10.58`, allow the `shopinventory` login into `shopinventory` and `shopinventoryweb`, and leave a narrow commented example for remote pgAdmin access.

After updating the file, reload PostgreSQL:

```sql
SELECT pg_reload_conf();
```

## Step 4: Create Roles And Databases

On the primary, create:

- an application login for ShopInventory
- a replication login for the standby
- the `shopinventory` and `shopinventoryweb` databases if they do not already exist

Use the exact host-specific SQL runbook here:

- [docs/operations/postgresql-roles-and-grants-10.10.10.9.md](docs/operations/postgresql-roles-and-grants-10.10.10.9.md)

That document includes:

- idempotent role creation for `shopinventory` and `repl_user`
- exact `CREATE DATABASE` statements for `shopinventory` and `shopinventoryweb`
- schema, table, sequence, and function grants for both databases
- replication validation queries for the primary and standby

## Step 5: Seed The Standby

Hard prerequisite: the primary and standby must already be on the same PostgreSQL major version.

With the current environment (`10.10.10.9 = 17`, `10.10.10.58 = 18`), this step is blocked until you either reinstall `10.10.10.58` to `17` or upgrade `10.10.10.9` to `18`.

If you use the primary-first upgrade path, complete this runbook first:

- [docs/operations/postgresql-primary-upgrade-10.10.10.9-17-to-18.md](docs/operations/postgresql-primary-upgrade-10.10.10.9-17-to-18.md)

Use the exact host-specific standby seeding runbook here:

- [docs/operations/postgresql-standby-seeding-10.10.10.58.md](docs/operations/postgresql-standby-seeding-10.10.10.58.md)

That document gives the full Windows PowerShell command set for `10.10.10.58`, including:

- stopping the PostgreSQL Windows service
- preserving the old data directory
- running `pg_basebackup` against `10.10.10.9`
- using `-R` so `standby.signal` and `postgresql.auto.conf` are written automatically
- starting the service again and validating streaming replication

## Step 6: Register Both Servers In pgAdmin

In pgAdmin, register both:

- `db-primary.internal`
- `db-standby.internal`

Use pgAdmin for:

- checking replication status
- confirming WAL replay is current
- running restores and validation queries
- handling role and database administration

Do not treat pgAdmin registration as failover configuration. It only helps you observe and manage the servers.

## Step 7: Generate The App Connection Strings

Once the two PostgreSQL hosts exist, generate the application connection strings with:

```powershell
.\scripts\New-PostgresHaConnectionStrings.ps1 `
  -PrimaryHost db-primary.internal `
  -StandbyHost db-standby.internal `
  -Username shopinventory `
  -Password "<app-password>"
```

This produces:

- an API connection string for `shopinventory`
- a Web connection string for `shopinventoryweb`

Both use Npgsql multi-host failover settings with `Target Session Attributes=read-write`.

## Step 8: Apply The New Connection Strings

Deploy them through the IIS deployment script:

```powershell
.\Update-Production.ps1 `
  -DeployTarget Both `
  -ApiDbConnectionString "<generated-api-connection-string>" `
  -WebDbConnectionString "<generated-web-connection-string>"
```

That path is required because ShopInventory production deployment preserves server-side `web.config` and now updates the DB values explicitly when overrides are provided.

## Step 9: Validate Before Cutover

Before calling the database HA work complete, verify:

1. The standby is streaming and replaying WAL.
2. The API and Web can connect using the generated multi-host strings.
3. `/health/ready` becomes healthy against the new database target.
4. EF migrations run cleanly against the new primary.
5. A forced primary outage or service stop can be recovered by promoting the standby.
6. After promotion, the API reconnects and the critical workers recover.

You can rerun `.\scripts\Test-PostgresWindowsHaReadiness.ps1` before and after the seeding step to confirm that the service, listener, firewall, version, and database-recovery state all match the expected topology.

## Manual Failover Baseline

If you do not yet have Patroni, repmgr, or Windows failover orchestration in place, use this baseline:

1. Stop the failed primary or isolate it from the network.
2. Promote the standby.
3. Confirm the promoted node is read-write.
4. Validate ShopInventory `/health/ready`.
5. Rebuild the old primary as the new standby before returning to a protected state.

Do not allow both nodes to become writable at the same time.

## Recommended First Production Version

For your current environment, the safest initial target is:

- native PostgreSQL on two Windows servers
- manual standby promotion
- WAL archiving to a network share
- ShopInventory pointed at the two-host Npgsql connection string
- pgAdmin used for visibility and administration

That gets you off localhost and removes the single-machine database dependency without forcing a more complex failover stack on day one.