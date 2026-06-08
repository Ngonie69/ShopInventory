# PostgreSQL HA And SAP Degradation Runbook

## Scope

This runbook defines the minimum HA baseline for the ShopInventory API and the operational rules that now exist in the application:

- PostgreSQL must support failover without changing application code.
- Backups must be verified by restore, not just by successful backup jobs.
- SAP outages must fail fast and degrade only through durable queues that already have production-grade processors.
- Health endpoints must remain truthful during dependency outages and saturation events.

## Recommended PostgreSQL Topology

Use a dedicated PostgreSQL HA pair plus a quorum witness:

- `db-primary`: read-write primary.
- `db-standby`: hot standby receiving streaming replication.
- `db-witness`: Patroni/repmgr witness, or the equivalent quorum node used by your failover tooling.

Recommended deployment rules:

- Put the primary and standby on separate VMs or physical hosts.
- Keep WAL archiving enabled so point-in-time recovery is available.
- Use synchronous replication only when the round-trip latency between database nodes is consistently low enough to keep write latency acceptable.
- If the standby is remote and synchronous commit is too expensive, use asynchronous replication and document the accepted RPO explicitly.
- Put failover under one control plane only. Preferred options are `Patroni`, `repmgr`, or a managed PostgreSQL HA service. Do not mix ad-hoc scripts with a second failover mechanism.

If your environment is native Windows with pgAdmin rather than Docker, use [docs/operations/postgresql-ha-windows-pgadmin.md](docs/operations/postgresql-ha-windows-pgadmin.md). pgAdmin is an admin client only; it does not provide replication or failover by itself.

## Connection String Failover Strategy

If the PostgreSQL setup supports direct multi-host connections from Npgsql, prefer a multi-host connection string so the API can reconnect after failover without config edits.

Example pattern:

```text
Host=pg-primary,pg-standby;
Database=shopinventory;
Username=app_user;
Password=***;
Target Session Attributes=read-write;
Load Balance Hosts=false;
Host Recheck Seconds=5;
Timeout=30;
Command Timeout=60;
Keepalive=60;
Maximum Pool Size=100;
Minimum Pool Size=10;
Connection Idle Lifetime=300;
Connection Pruning Interval=10;
Read Buffer Size=16384;
Write Buffer Size=16384
```

Guidance:

- `Target Session Attributes=read-write` prevents the API from attaching to a read-only standby for write traffic.
- `Host Recheck Seconds=5` keeps host health probing reasonably quick during failover.
- `Connection Idle Lifetime=300` and `Connection Pruning Interval=10` trim idle pooled connections without churning active traffic.
- `Timeout=30`, `Command Timeout=60`, and `Keepalive=60` balance failover detection with slower operational/reporting queries.
- Do not add `Multiplexing=true` to the shared application connection strings until the session-level advisory lock paths used by background-worker leadership and inventory locks have been separated or validated under multiplexing.
- If your HA layer exposes a single virtual IP, DNS name, or TCP proxy instead, keep using that single endpoint and let the HA layer move traffic.
- After any connection string change, verify EF migrations, startup readiness, and pooled connection recovery against a staged failover.

To generate the application connection strings for a direct primary/standby topology, use [scripts/New-PostgresHaConnectionStrings.ps1](scripts/New-PostgresHaConnectionStrings.ps1):

```powershell
.\scripts\New-PostgresHaConnectionStrings.ps1 `
	-PrimaryHost db-primary.example.local `
	-StandbyHost db-standby.example.local `
	-Username shopinventory `
	-Password "<db-password>"
```

The script outputs the API and Web connection strings in the exact format expected by the production startup guardrails.

## Deployment Integration

The IIS deployment script now accepts optional database override parameters and writes them into the inactive slot `web.config` before warm-up:

```powershell
.\Update-Production.ps1 `
	-DeployTarget Both `
	-ApiDbConnectionString "Host=db-primary.example.local,db-standby.example.local;Port=5432;Database=shopinventory;Username=shopinventory;Password=<db-password>;Target Session Attributes=read-write;Load Balance Hosts=false;Host Recheck Seconds=5;Maximum Pool Size=100;Minimum Pool Size=10;Connection Idle Lifetime=300;Connection Pruning Interval=10;Timeout=30;Command Timeout=60;Keepalive=60;Read Buffer Size=16384;Write Buffer Size=16384" `
	-WebDbConnectionString "Host=db-primary.example.local,db-standby.example.local;Port=5432;Database=shopinventoryweb;Username=shopinventory;Password=<db-password>;Target Session Attributes=read-write;Load Balance Hosts=false;Host Recheck Seconds=5;Maximum Pool Size=100;Minimum Pool Size=10;Connection Idle Lifetime=300;Connection Pruning Interval=10;Timeout=30;Command Timeout=60;Keepalive=60;Read Buffer Size=16384;Write Buffer Size=16384"
```

This is the supported path for replacing live production database settings, because the deployment flow preserves server-side `web.config` and now updates the connection strings explicitly when overrides are supplied.

## Backup Verification Standard

Backups are only considered valid when a restore has been completed successfully.

Minimum cadence:

- Every night: logical backup of schema and critical reference data.
- Every night: physical base backup or snapshot plus WAL archiving.
- Every week: restore the latest backup set to an isolated environment and run application smoke checks.
- Every month: full restore drill with measured RTO and RPO.

Verification checklist for the weekly restore:

- Restore to an isolated PostgreSQL instance.
- Start the API against the restored database.
- Start the Web app against the restored cache/customer-portal database when that surface is included in the backup set.
- Verify `/health/ready` and `/api/health` return healthy.
- Confirm required tables exist, including `BackgroundWorkerClusterStates`.
- Run a smoke query against invoice queue, transfer queue, and stock reservation tables.
- Record restore start time, restore end time, WAL replay end time, and the final recovery point.

## Application Startup Guardrails

The API and Web app now validate PostgreSQL connection strings at startup when `PostgresConnectionPolicy` is enabled in configuration:

- Production can be configured to reject `localhost`, `127.0.0.1`, `::1`, and `.` database hosts.
- Multi-host connection strings can be required to include `Target Session Attributes=read-write`.
- Unresolved placeholder connection strings fail fast instead of reaching runtime with an invalid database target.

Use these guardrails to keep new deployments pointed at the PostgreSQL HA endpoint or multi-host connection string rather than silently falling back to a single-machine database.

For native Windows hosts, you can preflight the database pair before the seeding or upgrade runbooks with:

```powershell
.\scripts\Test-PostgresWindowsHaReadiness.ps1 `
	-PrimaryHost 10.10.10.9 `
	-StandbyHost 10.10.10.58 `
	-IncludeDatabaseChecks
```

## Restore Drill Standard

Every monthly restore drill must answer three questions:

- How long until the database accepts connections?
- How long until the API readiness gate becomes healthy?
- How much data would be lost if production failed at the worst possible moment?

Drill steps:

1. Restore the latest base backup to an isolated database host.
2. Replay WAL to the desired recovery target.
3. Point a staging API instance at the restored database.
4. Confirm schema compatibility with `/health/ready`.
5. Confirm worker cluster state, invoice queue rows, and transfer queue rows are intact.
6. Record measured RTO and RPO.
7. Open follow-up work immediately if either metric exceeds the target.

## Feature Degradation Rules

These rules should drive both operator expectations and future implementation work.

### SAP-facing writes

- Desktop direct invoice creation now degrades to the durable invoice queue when SAP is unavailable. The reservation is renewed so stock is not released before queued processing can resume.
- Desktop direct stock transfer creation now degrades to the durable transfer queue when SAP is unavailable.
- API inventory transfer creation now degrades to the durable transfer queue only when the payload has no serial-number allocations. Serial-number transfers still fail fast because the queue contract does not safely preserve that detail yet.
- Incoming payments now degrade to the durable incoming-payment queue when the SAP circuit is open or SAP failures are transient.
- Purchase invoices and GRPO writes still fail fast when SAP is unavailable. They do not yet have a mature durable queue path and must not pretend to accept work that cannot be processed safely.

### Reporting and read-only features

- Prefer existing cached or replicated data sources where those surfaces already exist.
- Do not force reporting endpoints to synchronously call SAP in the request path when a cache is already available.
- If a report must show stale data, label it clearly in the UI and expose the data age.

### Non-SAP features

- Authentication, audit views, local configuration, queue inspection, and health endpoints must remain available when SAP is down.
- Readiness and dependency health must report the actual degraded state without taking the entire process offline unless the readiness contract itself is broken.

## Saturation And Alert Thresholds

The API now exposes the following health signals and these should be wired into monitoring:

- `db-latency`: degraded at 500 ms connection acquisition, unhealthy at 2 s.
- `thread-pool`: degraded when worker utilization is high or pending work items exceed 500, unhealthy at severe saturation.
- `queues`: degraded when queue age exceeds 15 minutes or queue depth exceeds 25, unhealthy at 60 minutes or 100 items.
- `sap`: unhealthy when the SAP circuit breaker is open or the SAP dependency check fails.
- `workers`: unhealthy when a critical background worker has no healthy cluster leader.

Alert on age as well as count. Queue count alone misses stuck processors.

## Failover And Handoff Tests

Run these tests as scheduled operations, not only during incidents:

- PostgreSQL failover test: at least quarterly.
- Backup restore verification: weekly.
- Full restore drill: monthly.
- Background worker leader handoff: after every HA deployment change and at least monthly.

Use [scripts/Test-WorkerLeaderHandoff.ps1](../../scripts/Test-WorkerLeaderHandoff.ps1) to recycle the active IIS node for a worker and verify that a different node becomes leader within the agreed threshold.

## Current Limitation

This repository can now generate and deploy PostgreSQL HA connection strings, but it cannot provision the actual primary, standby, or witness hosts from this workspace alone. Standing up the real database cluster still requires infrastructure access, PostgreSQL host provisioning, replication setup, WAL archiving, and failover tooling on those database hosts.