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
