# Changelog

Changes that callers outside this repository have to act on: endpoints removed or renamed, request
or response shapes changed, and behaviour changes that would surprise somebody who had read the old
documentation.

There is no version number and no release tag — deploys run continuously from `main` — so entries
are dated by the day they merged, newest first. `Unreleased` is what is merged to `main` but not yet
deployed to production.

Ordinary features and fixes are not listed here; the git history is the record for those. Something
belongs in this file when a client that worked yesterday needs changing, or when an operator would
otherwise be surprised.

---

## Unreleased

### Added

- **`GET /api/DesktopIntegration/transfer-listener/status` and
  `POST /api/DesktopIntegration/transfer-listener/check-now`** (Admin, Manager).

  Until now TransferEventListener called this API and this API never called back, so a listener
  that had stopped reading SAP was indistinguishable from an afternoon with no transfers in it —
  while every transfer made in the meantime was missing from the day's snapshot and a till was
  refusing stock the warehouse held. `status` answers `200` with `reachable: false` when the
  listener is down rather than failing, because that is the answer worth reading. `check-now` is a
  write on the listener: it advances its poll window and delivers webhooks for anything new.

  Both are surfaced at `/transfer-listener` in the Web app.

- **A `transfer-listener` check on `/health/dependencies`.** Anything monitoring that endpoint
  will see a new entry, and will see the whole endpoint report `Degraded` or `Unhealthy` in
  situations that previously reported healthy — because they always were these situations and
  nothing was looking. `Degraded` covers a poll that is merely late, since the listener re-reads
  the same window next cycle; `Unhealthy` covers a listener that is unreachable or not polling,
  and webhook deliveries that failed and are never retried.

  It reports one condition that had been true all along: warehouses in `DailyStock:MonitoredWarehouses`
  that the listener does not watch. `KEFBYS` was one — the Bulawayo shop was snapshotted at 07:00
  and never adjusted again while stock moved through it all day, because the two warehouse lists are
  maintained independently. **That is fixed in the listener** (it now watches KEFBYS), so the two
  lists match and the check reports healthy; the comparison stays because nothing else would catch
  the next omission.

  This check requires the listener to be running a build that has `GET /api/Transfer/health`
  (added in the TransferEventListener repository at the same time). Against an older listener the
  probe gets a `404` and reports `Unhealthy`, so deploy the listener first.

### Fixed

- **`DailyStock:MonitoredWarehouses` was binding to every warehouse twice**, so the 07:00 stock
  snapshot job walked each monitored warehouse twice and made double the SAP reads it needed —
  against a pool of six concurrent SAP requests where stock reads already hang for minutes.

  The cause is a shape, not a typo: the ASP.NET Core configuration binder **appends** to a collection
  that already holds items rather than replacing it, for `List<T>` and `T[]` alike. A settings
  property declared with a collection initializer *and* supplied in appsettings.json therefore holds
  both copies. The same shape was in `OpenWA:WebhookEvents` and `OpenWA:HealthEndpointPaths` (no
  symptom — their readers already deduplicate) and in TransferEventListener's
  `SapSettings.FallbackScanItemGroups`. All four now declare `= []` and take their values from
  configuration alone.

  **Operators:** these lists no longer have a code-level default, so configuration is the only source.
  `DailyStock:MonitoredWarehouses` is now validated at startup — the API **refuses to start** if it is
  missing or empty, rather than silently snapshotting no warehouses and leaving every till refusing
  every sale. A duplicated entry fails startup too. Nothing changes for a correctly configured
  environment; `appsettings.json` already carries all four lists.

  `scripts/find_double_bound_options.py` finds this shape in any repo, and
  `OptionsCollectionBindingTests` fails the build if a new settings class introduces it.

### Changed

- **The morning stock snapshot falls back to TransferEventListener** when this API's own non-batch
  stock read fails, controlled by `TransferEventListener:UseForUnbatchedStockFallback` (on by
  default). The listener holds its own Service Layer session, so it can answer while all six of
  this process's SAP slots are held by a hung read. A snapshot that previously shipped with only
  batch-managed rows may now be complete; either way the snapshot's `LastError` records which read
  answered, including when the reading came from the listener's degraded item scan and may be
  short.

### Deprecated

- **`POST /api/RateLimit/unblock/{clientId}` still works; prefer
  `POST /api/RateLimit/reset/{clientId}`.**

  They are now one action with two routes rather than two implementations, so they cannot answer
  differently: same `200 {"message": …}`, same `404` for a client id nothing has counted. Existing
  callers need do nothing. New callers should use `reset`.

  Nothing in this repository calls `unblock` — no Web page, no service, only the catalogue entries
  describing it — and it was briefly deleted during development on that basis. It was restored
  because the versioning policy in [API.md](API.md#api-versioning) keeps version `1.0` endpoints
  working for clients that already call them, and an admin tool or script outside this repository
  could be one of those. No caller was ever affected: the deletion did not reach a deploy.

### Changed

- **The daily stock snapshot now runs.** `DailyStock:EnableAutoStockFetch` was `false`, so the
  Quartz job that builds each morning's snapshot was never registered and no snapshot was ever
  written. `GET /api/DesktopIntegration/stock/{warehouseCode}/local` answered
  `404 DesktopSales.SnapshotNotFound` for every monitored warehouse, all day, every day.

  It answers with the warehouse's stock once the fetch has run, and still answers `404` before
  that — the snapshot is built at 07:00 CAT, so an early-morning caller sees the same 404 as
  before and should keep whatever fallback it has. A client that read that 404 as “this
  warehouse is empty” rather than “today's figures are not ready yet” will start seeing stock
  where it saw none.

  A till validating a sale against the snapshot was validating against nothing, so quantities that
  were refused or waved through on an empty snapshot will now be checked against real stock. SAP
  is read once per monitored warehouse each morning, which is load that was not there yesterday.

- **`POST /api/RateLimit/reset/{clientId}` now lifts the client's block**, not just its request
  counter. It previously left `isBlocked` and `blockExpiresAt` untouched, so resetting a blocked
  client left it blocked — the one state anybody resets a client in. Anyone who was calling `reset`
  and then `unblock` to work around it can drop the second call.

  `totalBlockedCount` is still left alone by design: it is the client's history, and it is what says
  a client needs a conversation rather than another reset.

- **`PUT /api/RateLimit/config` now changes the rate limits that are actually applied.** It
  previously answered `200 … updated successfully` and changed nothing: the values were written to a
  scoped service and discarded with the request, and the limiter that returns `429` never read them.
  Limits are now stored and picked up by every instance within about 10 seconds, with no restart.

  Operationally this means a value sent to that endpoint **will now take effect**, where before it
  was inert. Two consequences worth knowing:

  - Out-of-range values are refused with `400 RateLimit.InvalidConfiguration` rather than accepted.
    A permit limit below 1 or a zero-length window would make the limiter throw on every request.
  - Changing a limit gives every client a fresh window, so limits are briefly more generous
    immediately after a change.

  `isEnabled`, `whitelistedIPs` and `whitelistedApiKeys` also do something now, having previously
  been accepted and ignored. Note that `isEnabled: false` does not switch rate limiting off — it
  stops partitioning unauthenticated callers per IP, which puts the whole internet in one shared
  bucket. See the Rate Limit Management section of [API.md](API.md) before using it.
