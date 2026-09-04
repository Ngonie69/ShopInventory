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

### Removed

- **`POST /api/RateLimit/unblock/{clientId}` is gone. Use `POST /api/RateLimit/reset/{clientId}`.**

  The two did the same thing. Same verb, same route shape, same `200 {"message": …}` answer and the
  same `404` for a client id nothing has counted, so the migration is the URL and nothing else.

  Nothing in this repository called it — no Web page, no service, only the catalogue entries
  describing it. Anything outside this repository that calls it will get a `404` after this deploy.
  The endpoint required the `users.edit` permission, so a caller would be an admin tool or a script
  rather than a handset app.

  > **This is a breaking change to a version `1.0` endpoint**, which the versioning policy in
  > [API.md](API.md#api-versioning) says should instead go in a new API version with `1.0` kept
  > working. It was removed outright. If any external caller turns out to depend on it, restoring it
  > as a thin alias for reset is a small change.

### Changed

- **`POST /api/RateLimit/reset/{clientId}` now lifts the client's block**, not just its request
  counter. It previously left `isBlocked` and `blockExpiresAt` untouched, so resetting a blocked
  client left it blocked — the one state anybody resets a client in. Anyone migrating from
  `unblock/{clientId}` gets the behaviour they had; anyone who was calling `reset` and then
  `unblock` can drop the second call.

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
