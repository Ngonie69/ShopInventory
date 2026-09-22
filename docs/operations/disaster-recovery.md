# Disaster Recovery

What protects production today, how to check it, and what to do when something is lost. For the
target design (HA pair, WAL archiving, drill cadence) see [postgresql-ha.md](postgresql-ha.md).
That page describes a plan; this one describes what is actually running.

## What protects production today

Measured on 2026-09-22 by running the two scripts below on `10.10.10.9`. Rerun them before relying
on any row. They change nothing.

| | State | Protects against |
|---|---|---|
| Streaming standby | `10.10.10.58` streams from `.9`, asynchronous, ~1 ms behind, slot `shopinventory_failback_1058`. It copies every database on the instance. | Losing `.9` (disk, VM, host). **Not** bad data: a wrong `DELETE` reaches `.58` in milliseconds. |
| WAL archiving | **Off.** `archive_mode=off`, nothing ever archived. | Nothing. There is no point-in-time recovery. |
| Nightly database dump | **None.** | Nothing. |
| AOMEI Backupper (Technician Plus 7.1.1) | One task, `System Backup(1)`, to `\\10.10.10.254\svr_backup\AOMEI_DATA\internaldev20241028`. **Last run 28/10/2024**, shows a warning. | Nothing current. |
| In-app backups (Settings → Backups) | Four, made by hand, last 2026-09-07. About ten small tables (users, config, webhooks, audit). Stored on `.9`'s own `C:`. `Restore` validates the zip and writes nothing back. | Nothing you could recover the business from. |
| VMware snapshots | `.9` is a VMware VM. Whether anything snapshots it is **unknown**. | Unknown. Ask whoever runs vSphere. |
| SAP | System of record for every document that reached it: invoices, credit notes, transfers, payments. | Posted documents. Not ShopInventory's own data: queues, van and desktop sales not yet posted, reservations, users, routes, config, fiscal state. |

In one line: **a dead `.9` is survivable by hand; a bad write is not.**

### Check it again

On `10.10.10.9`, in an elevated Windows PowerShell 5.1 session:

```powershell
.\scripts\Get-DisasterRecoveryState.ps1   # database, standby, archiving, backups, Quartz members
.\scripts\Get-AomeiBackupState.ps1        # AOMEI task, destination, newest image, results
```

Each writes a transcript beside itself. Things to read first:

- `standbys streaming from this server`: one `walreceiver` row from `10.10.10.58`, state `streaming`.
  An empty table means the standby is gone.
- `replication slots`: `retained_wal` near 0. A growing number means `.58` stopped reading and `.9`
  is keeping WAL for it. `.9` has one 300 GB `C:` drive, and a full disk stops the primary.
- `Quartz cluster members` and `ClusterNodes`: which machines run background jobs, and on which build.

## Before you need it

These live only on `.9`. If the VM is lost, so are they. Copy them somewhere safe now, and again after
any change. They are secrets, so the copy needs the same care as a password vault.

- `web.config` of each live slot: `C:\inetpub\ShopInventory-API-Blue|Green` and
  `C:\inetpub\ShopInventory-Web-Blue|Green`. They hold the connection strings, the customer-portal
  signing secret, `OpenWA__*`, the SAP settings and the API keys.
- `firebase-service-account.json` from the live API slot (push notifications to the van app).
- The deploy credential files the runner uses (see
  [github-actions-deploy-runner.md](github-actions-deploy-runner.md)).
- The TransferEventListener install and config (`10.10.10.9:5050`, separate repo).
- The OpenWA install, if it runs on `.9` (see [whatsapp-production.md](whatsapp-production.md)). A
  new host will need the handset re-paired.

Also on `.9`, and also lost with it: `workshopdb` (~2 GB, the KefalosWorkshop app), `FDMS`,
`security3`. The standby copies them, since it replicates the whole instance, but nothing else does.

## Scenario A: `.9` is lost

Use this when `.9` is gone or cannot be brought back within an acceptable time: VM deleted, disk
dead, host unrecoverable, or ransomware on its file system. File-level ransomware does not spread
to `.58`, because physical replication ships WAL records, not files.

**Do not start this if `.9` might come back on its own.** Two writable primaries split the data,
and nothing merges them afterwards.

### 1. Fence `.9`

Make sure `.9` cannot accept writes before `.58` does:

- If `.9` still boots: `Stop-Service postgresql-x64-17` and stop both IIS app pools
  (`ShopInventoryAPI*`, `ShopInventoryWeb*`), then set the PostgreSQL service to **Disabled** so a
  reboot does not bring it back.
- If it does not: power the VM off in vSphere and disconnect its network adapter.

### 2. Check what `.58` holds

In `psql` on `.58`, against any database:

```sql
SELECT pg_is_in_recovery()             AS still_standby,   -- expect t
       pg_last_wal_receive_lsn()       AS received,
       pg_last_wal_replay_lsn()        AS replayed,        -- expect = received
       pg_last_xact_replay_timestamp() AS last_commit_replayed;
```

`last_commit_replayed` is the last moment of data you will have. Replication is asynchronous, so
anything committed on `.9` after it is lost. Record it; it is your measured RPO.

If `replayed` is behind `received`, wait for it to catch up before promoting.

### 3. Promote `.58`

```sql
SELECT pg_promote();          -- waits up to 60 s, returns t on success
SELECT pg_is_in_recovery();   -- must now be f
```

`.58` is now the primary. From here, `.9` must never start as a primary again (step 1 and step 8).

### 4. Put current code on `.58`

`.58` has ShopInventory API and Web sites in IIS, but on an **old build**: deploys have skipped it
since 2026-09-14 (`ADDITIONAL_PRODUCTION_SERVERS=none`). Its Web slot's `web.config` points at
**`.9`'s** `ShopInventoryWeb` database. Both must change before it takes traffic.

The runner lives on `.9`, so it went with it. Deploy by hand from a machine that can open a
deployment session to `.58`. `.58` is not domain-joined, so that machine needs `10.10.10.58` in its
`TrustedHosts` (see "Troubleshooting" in the runner doc):

```powershell
.\Update-Production.ps1 `
    -ProductionServer 10.10.10.58 `
    -DeployTarget Both `
    -ApiDbConnectionString "Host=localhost;Port=5432;Database=ShopInventory;Username=<user>;Password=<password>;..." `
    -WebDbConnectionString "Host=localhost;Port=5432;Database=ShopInventoryWeb;Username=<user>;Password=<password>;..."
```

- Take the rest of each string (pool sizes, timeouts) from the saved `.9` `web.config`.
- The Web database is **`ShopInventoryWeb`**, capital letters. A lowercase `shopinventoryweb` also
  exists and is stale; it is only named in a `web.config` left over from May.
- The deploy applies EF migrations. The promoted database is at `.9`'s schema, so there should be
  none. If it reports any, stop and find out why.
- Copy the saved secrets into `.58`'s live slots: the `web.config` values the deploy does not
  set itself, and `firebase-service-account.json` into the API slot.

### 5. Move traffic

`sis.kefaloscheese.com` resolves on the LAN to `10.10.10.10`, an nginx box that forwards to `.9`.
Change its upstream to `10.10.10.58` and reload nginx. **Its config has never been read into this
repo**: find the file and write the path here the first time you do this.

### 6. Bring back the other `.9` services

- **TransferEventListener**: install on `.58`, or on another host, and point its ShopInventory URL
  at the new API. Stock snapshots stop adjusting to SAP transfers until this is back.
- **OpenWA**: start it on `.58`, re-pair the handset, and recreate the webhook (row 4 of
  [whatsapp-production.md](whatsapp-production.md)). A paired session with no webhook looks healthy
  and delivers nothing.
- **Deploy runner**: register a runner on `.58` and change the workflow's primary server, or deploy
  by hand until `.9` is rebuilt. Until one of those is done, merges to `main` do not deploy.

### 7. Verify

- `https://sis.kefaloscheese.com/health/ready` says `Healthy`, and `/api/health` shows every check.
- `Get-DisasterRecoveryState.ps1`, run on `.58`, shows `is_standby = f`, `ClusterNodes` with `.58`
  on the new build stamp, and `.58` as the only Quartz member.
- A till sale and a van sale reach SAP. The durable queues (invoice, transfer, incoming payment)
  drain.

### 8. Rebuild `.9` as the new standby

Once `.9` is repaired or replaced, seed it from `.58` with
[postgresql-standby-seeding-10.10.10.58.md](postgresql-standby-seeding-10.10.10.58.md), with the two
hosts swapped. Never start `.9`'s old data directory as a primary: it forked from `.58` at the
promotion and is wrong from that point on.

## Scenario B: bad data

A bad migration, a mass update or delete, a script run against the wrong database, or a malicious
change through the application.

**Today there is no restore point.** The standby already holds the damage. What exists:

- **SAP** holds every document that was posted. ShopInventory data that mirrors SAP (documents,
  business partners, items, prices) can be rebuilt from it by the sync jobs.
- **The in-app JSON backups** hold users, config, webhooks and audit logs as of their date, and
  nothing else. They have to be reloaded by hand; the app's restore does not write.
- **The audit log and application logs** may be enough to reverse a narrow change by hand.

What to do now:

1. Stop the damage from spreading: stop the Quartz-running API app pool if a job is doing it, or
   revoke the account or key making the changes.
2. Work out the scope: which tables, which rows, since when.
3. Repair from SAP, from the audit log, or by hand. Record what could not be recovered.

Once nightly dumps exist, the procedure becomes: restore the last good dump to a new database name,
compare, then copy the affected rows back or swap the databases. Write that down here when the
dump job is in place, and prove it with a restore before calling it done.

## Scenario C: `.58` is lost

Production is unaffected, but it now has no standby, and `.9` keeps WAL for the slot
`shopinventory_failback_1058` until the slot is dropped or `.58` returns.

1. Watch `retained_wal` in the replication-slot section of `Get-DisasterRecoveryState.ps1`. `.9`
   had 131 GB free on 2026-09-22.
2. If `.58` will be back within days, leave the slot so it can catch up without reseeding.
3. If not, drop the slot on `.9`, as an operator decision:
   `SELECT pg_drop_replication_slot('shopinventory_failback_1058');`
4. Rebuild with [postgresql-standby-seeding-10.10.10.58.md](postgresql-standby-seeding-10.10.10.58.md).

## Scenario D: SAP is down

Not a data-loss event. ShopInventory degrades through its durable queues. See "Feature Degradation
Rules" in [postgresql-ha.md](postgresql-ha.md).

## Open items

In the order they close the biggest gap. Update this page as each one lands.

1. **Nightly `pg_dump`** of `ShopInventory` and `ShopInventoryWeb` (and `workshopdb` if the Workshop
   app's owner agrees) to `\\10.10.10.254\svr_backup`, keeping 14 days, with an alert when the
   newest dump is over 26 hours old. Then restore one to prove it works.
2. **WAL archiving** to storage off `.9`, which gives point-in-time recovery. Needs a PostgreSQL
   restart on `.9`.
3. **AOMEI**: find out why `System Backup(1)` stopped in October 2024, and either schedule it
   (weekly whole-server image) or remove it so it stops looking like protection.
4. **VMware**: find out whether `.9` and `.58` are snapshotted, how often, and how long snapshots are
   kept.
5. **A copy outside the building.** `.9`, `.58` and the NAS may share one site.
6. **The nginx config** on `10.10.10.10`: read it and record the upstream in step 5 above.
7. **A drill.** Scenario A has never been run. Do it once against a copy, and record the time to
   `Healthy` and the RPO from step 2.
