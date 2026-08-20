# Production log review — 2026-08-20

Source: `shopinventory-api-20260820.log` — the `ShopInventory` API on production.
Window: `00:01:28` → `09:11:54 +02:00` (9h 10m).
Volume: 8,361 lines — 8,196 `[INF]`, 52 `[WRN]`, 9 `[ERR]`, 104 stack-trace continuations.

Findings are ordered by impact, not by log level: **every one of the 9 `[ERR]` lines is a business
refusal that behaved correctly**, and the things that actually cost users time were logged at `[INF]`.

---

## Summary

| # | Finding | Severity | Evidence in log | Status |
|---|---------|----------|-----------------|--------|
| 1 | Credit refusals logged as `[ERR]` with a stack trace and a false reason | High | 9/9 errors | **Fixed** |
| 2 | Duplicate POD uploads reach storage, notifications and push | High | 4 of 24 uploads (17%) | **Fixed** |
| 3 | Refresh-token rotation race logs users out | High | 2 storms, 5 failed refreshes | **Fixed** |
| 4 | Client IP recorded as `::1` for browser users, including failed logins | High | 9 of 32 auth events | **Fixed** |
| 5 | BP price lookup with no item codes takes a 20s+ full-list path | Medium | 6 stalls; caused the 08:10 outage | **Fixed** |
| 6 | Order approval re-prices against live SAP before the credit gate | Medium | SPA077 priced 5x to be refused 4x | **Fixed** |
| 7 | POD upload-status report: 20–64s cold builds, no pre-warm | Medium | 6 cold builds in 90 min | **Fixed** |
| 8 | Full price catalog sync: 6–7 min, 3x/day, identical output each run | Medium | 418s / 381s / 375s | Part |
| 9 | Reconciliation chases a dead order every 2 min forever, at `[INF]` | Medium | 276 sweeps, 3+ days on one order | **Fixed** |
| 10 | 60s stock query failed with **zero** log output | Medium | 1 occurrence | **Fixed** |
| 11 | Health alerting flaps: 2 emails + 2 pushes for a 5-min self-healing blip | Low | 08:11 → 08:16 | **Fixed** |
| 12 | `FCM send failed … : null` — unusable diagnostic, dead token not pruned | Low | 1 occurrence | **Fixed** |
| 13 | 15 notifications to role `Cashier` reached no devices | Low | 15 occurrences | **Fixed** |
| 14 | 76% of the log is mechanical chatter | Low | 6,345 of 8,361 lines | **Fixed** |

**Not a problem, despite appearances:** see [Appendix A](#appendix-a--the-sap-query-name-churn-is-working-as-designed).

**All fourteen findings are addressed bar one half.** Weeks 1–3 and the backlog are done; the only
outstanding work is the incremental price sync in finding 8, which needs a check against production
SAP before it can be written safely — see [What Week 3 changed](#what-week-3-changed).

Measured effect per phase: [Week 1](#what-week-1-changed), [Week 2](#what-week-2-changed),
[Week 3](#what-week-3-changed), [the backlog](#what-the-backlog-changed).

---

## 1. Every `[ERR]` in the file is a credit refusal that worked — High

All nine errors are the same shape:

```
07:39:36 [ERR] Failed to approve sales order 2862 (SO-20260820-0001) because posting to SAP failed. Approval state was rolled back.
ShopInventory.Services.CreditLimitExceededException: This order would take Spar Avondale (SPA077) over its credit limit. …
   at SalesOrderService.EnsureWithinCreditLimitAsync(…) line 2851
```

Nothing was posted to SAP. `EnsureWithinCreditLimitAsync`
(`ShopInventory/Services/SalesOrderService.cs:1681`) refuses the order *before* the SAP create,
exactly as designed — and the comment above that gate says so. The refusal then falls into the
catch-all at `SalesOrderService.cs:1289`, which logs `LogError` with the stack at line 1329 and
rethrows.

`ApproveSalesOrderHandler` already handles this correctly
(`ShopInventory/Features/SalesOrders/Commands/ApproveSalesOrder/ApproveSalesOrderHandler.cs:141`):
it catches `CreditLimitExceededException` and logs a warning. So the log carries the same refusal
three times, once at the wrong level with a wrong reason:

```
07:39:36 [WRN] Refused a 1050.48 sales order on SPA077: the account would reach 5264.41 against a 5000.0 limit
07:39:36 [ERR] Failed to approve … because posting to SAP failed.        <- wrong level, wrong reason
07:39:36 [WRN] Refused approval of sales order 2862 on credit: …
```

**Why it matters.** `[ERR]` is the only level an operator can alert on. Today it fires on a
salesperson hitting a credit limit — a normal, daily, self-correcting event — so any alert built on
it is noise and will be muted. Meanwhile the genuinely broken things in this log (findings 9 and 10)
never reach `[ERR]` at all.

**Fix.** In `ApproveAsync`, add an exception filter ahead of the catch-all that performs the same
rollback but logs at `Information` with the business reason, then rethrows for the handler to convert
to an `ErrorOr` result:

```csharp
catch (CreditLimitExceededException) { /* rollback */ throw; }   // no LogError, no stack
catch (Exception ex) { /* rollback */ _logger.LogError(ex, "…"); throw; }
```

Drop the duplicate `Refused a … sales order on …` warning from `CreditLimitService` down to `Debug`;
the handler's `Refused approval of sales order {OrderId} on credit: {Reason}` already carries the
full message.

**Secondary.** The capture-time and approval-time checks report different precision for the same
number — `1050.484050000000000000` at 07:27 vs `1050.48` at 07:39 — because the mobile path passes a
computed total and the approval path passes the persisted, rounded `order.DocTotal`. Same service,
different inputs. Round before the comparison so an order cannot pass one gate and fail the other by
fractions of a cent.

**Effort:** ~1 hour. **Test:** existing `MobileOrderCreditHoldTests`; add one asserting no `[ERR]` is
emitted for a credit refusal.

---

## 2. Duplicate POD uploads survive to storage, notifications and push — High

Four of the day's 24 POD uploads were submitted twice:

| Invoice | Gap | Caught by dedupe? |
|---|---|---|
| 2229934 | ~5 ms | Yes — `ExternalReference` concurrent guard |
| 2232744 | 2 s | **No** |
| 2227977 | 73 s | **No** |
| 2229376 | 77 s | **No** |

The 2229934 case shows what the duplicate costs even when the *attachment* is deduped:

```
06:37:05 Reused attachment 19106 after a concurrent duplicate upload …
06:37:05 Created notification: POD Uploaded: invoice 758051 for Lovemore Taundi
06:37:05 Created notification: POD Uploaded: invoice 758051 for Lovemore Taundi   <- twice
06:37:07 Push notification 43922 sent to 1 device(s)
06:37:07 Push notification 43921 sent to 1 device(s)                              <- twice
```

`DocumentService.UploadAttachmentAsync` has two guards (`ShopInventory/Services/DocumentService.cs:497`
on `ExternalReference`, `DocumentService.cs:576` on a SHA-256 within a 15-minute window). Both are
per-attachment. Neither caught the three uploads seconds-to-minutes apart, because a second tap
produces a fresh capture: a different external reference *and* different bytes. Content hashing
structurally cannot catch this.

**Fix, in two parts.**

1. **API — a third guard, on intent rather than content.** Refuse (or reuse) a POD for the same
   `(EntityType, EntityId, UploadedByUserId)` within a short window (2–5 min) unless the caller
   explicitly flags an additional page. This is the only guard that can catch a re-tap.
2. **API — move the side effects behind the guard.** Whichever guard fires, `UploadPodHandler` must
   return the reused attachment *without* raising a notification or a push. That fix alone removes the
   duplicate messages even while part 1 is in flight, and it is the cheaper half.

The handset side (RFDBS/Cheeseman, separate repo) should disable the upload control once a POD is
recorded for an invoice; note it, but do not block on it.

**Effort:** ~half a day for part 2, ~1 day for part 1.
**Test:** integration test — two uploads for one invoice by one user, 30s apart, assert one attachment
and one notification.

---

## 3. Refresh-token rotation has no grace window, and clients refresh in parallel — High

Two distinct storms, one root cause each:

```
07:59:37.691 [INF] Tokens refreshed for user Wellington Moyo from IP: 102.128.79.150
07:59:37.741 [WRN] Inactive refresh token used from IP: 102.128.79.150. Expired: false, Revoked: true
```

50 ms apart, same IP, same user: two requests raced, the first rotated the token, the second presented
the now-revoked one and was rejected. `Expired: false, Revoked: true` is the signature.

```
09:02:16–18  4x AuthenticationScheme: ApiKey was challenged.
09:02:18     4x Handling RefreshTokenCommand
09:02:19     4x [WRN] Inactive refresh token used from IP: 10.10.11.27
09:02:20–21  8x AuthenticationScheme: ApiKey was challenged.
```

An access token expired, four in-flight requests 401'd simultaneously, all four fired a refresh, all
four failed, and the client retried into another eight 401s. The user was logged out.

`AuthService.RefreshTokenAsync` (`ShopInventory/Services/AuthService.cs:227`) rejects any non-active
token outright. But it already writes `token.ReplacedByTokenHash` at line 252 — the successor is on
record.

**Fix.** Add a reuse grace window: if the presented token is revoked, was revoked within the last
~60 seconds, and has a `ReplacedByTokenHash`, return the tokens for that successor instead of failing.
Outside that window, keep the current behaviour — a genuinely reused old token is a credential-theft
signal and should still be refused (and should revoke the whole chain).

That fixes the race. The parallel-refresh storm also wants a single-flight refresh on the client, but
the grace window makes the storm harmless on its own, which is the right order to do it in.

**Effort:** ~half a day. **Test:** two concurrent `RefreshTokenAsync` calls with the same token both
return valid tokens; a third call 5 minutes later is refused.

---

## 4. Browser users are logged as `::1` — High (security/audit)

Nine of the 32 auth events in the log record the client IP as `::1`:

```
07:55:15 [WRN] Failed login attempt with wrong password for user: Crispen Mambeya from IP: ::1
08:03:30 [WRN] Failed login attempt with wrong password for user: Crispen Mambeya from IP: ::1
```

Both failed logins in the whole file are unattributable. Handset traffic records real addresses
(`197.211.249.251`, `77.246.50.237`); browser traffic does not, because it arrives through the
co-located Blazor Server Web app, which calls the API server-to-server.

`ForwardedHeadersOptions` is configured correctly on the API (`ShopInventory/Program.cs:177`, applied
at `Program.cs:931`). The gap is on the Web side: only
`ShopInventory.Web/Services/SalesOrderService.cs:351` and the Swagger proxy forward `X-Forwarded-For`.
The auth calls do not.

**Why it matters.** Rate limiting, lockout and every audit trail key on
`HttpContext.Connection.RemoteIpAddress` (24 call sites). With every browser user presenting as
loopback, an IP-based lockout either locks out every web user at once or is effectively disabled, and
a brute-force attempt through the web UI is invisible.

**Fix.** Forward the browser's address on every Web→API call — a `DelegatingHandler` on the Web's API
`HttpClient` that copies the current `HttpContext`'s remote address into `X-Forwarded-For`, rather
than the current per-call-site approach, which has already drifted. Confirm
`ReverseProxy:KnownProxies` in production `appsettings` (it appears in no committed settings file, so
the default loopback-only trust is in force — correct for this topology, but it should be explicit).

**Effort:** ~half a day. **Verify:** log in through the Web UI with a wrong password and confirm the
warning carries the browser's real address.

---

## 5. A BP price lookup with no item codes takes the 20-second full-list path — Medium

```
08:06:59 [INF] Handling GetPricesByBusinessPartnerQuery
08:07:19 [WRN] SQL query path for price list 9 exceeded its 20s budget; falling back to the Items API …
```

`ShopInventory/Features/Prices/Queries/GetPricesByBusinessPartner/GetPricesByBusinessPartnerHandler.cs:101`
branches on whether the caller supplied item codes. With codes, it takes the fast targeted path
(`Targeted Items API lookup retrieved 26 prices` — ~500 ms). Without them, it calls
`GetPricesByPriceListAsync`, which is the whole-catalogue path with a 20-second budget and an Items
API fallback behind it.

This fired six times between 08:06 and 08:10 — every one of them chained off an interactive pricing
lookup, none inside a scheduled sync window — and is what tipped SAP over:

```
08:10:34 [WRN] Transient SAP error … price list 101 at skip 3500 … forcibly closed by the remote host
08:10:34 [WRN] Transient SAP error … price list 101 at skip 300  … forcibly closed by the remote host
08:10:35 [WRN] Transient SAP response "BadGateway" … price list 101 at skip 0
08:11:31 [WRN] System health is "Degraded" — sending failure alert
```

**Fix.** Two changes, both small:

1. Never serve a user-facing request from the full-list path. When no item codes are supplied, answer
   from the locally cached catalogue (`GetCachedPricesQuery` already does this in ~120 ms) and let the
   scheduled sync own the SAP pull.
2. Give the interactive path a much tighter budget than the batch sync's 20s —
   `PriceListSqlRequestTimeoutSeconds` is a single setting used by both
   (`ShopInventory/Services/SAPServiceLayerClient.cs:519`). Split it: ~3s interactive, keep 20s for
   the sync.

**Effort:** ~1 day. **Verify:** re-run the shape from 08:06:59 and confirm sub-second response.

---

## 6. A refused approval still pays for a full live SAP re-pricing — Medium

Order 2862 (SPA077) was approved four times and refused four times. Each attempt re-priced the order
against live SAP first:

```
07:39:35 Populating prices for order 2862 using live SAP BP pricing for 26 items (BP: SPA077)
07:39:36 Prices populated for order 2862: SubTotal=909.51, Tax=140.97, Total=1050.48
07:39:36 [WRN] Refused a 1050.48 sales order on SPA077 …
```

Five pricing round-trips to produce four refusals, two of which burned the 20s SQL budget in
finding 5. Approvals across the day took 3.6s to 25.6s (`Sales order approval completed … in
25609 ms`).

The ordering is deliberate and correct — the comment at `SalesOrderService.cs:1674` explains that an
unpriced line understates `DocTotal`, so credit must run *after* pricing. The waste is that the
approver was never told the order could not succeed before they clicked.

**Fix.** Show credit headroom on the approval screen — the order total, the account's current exposure
and the limit — computed from the already-cached balance when the list loads. The refusal message
already contains everything needed (`USD 264.41 over`); it just arrives 8 seconds too late and only
after a wasted SAP call. This is a Web change, not an API one.

**Effort:** ~1 day. **Verify:** an over-limit order shows the shortfall in the list, before approval.

---

## 7. The POD upload-status report costs 20–64 seconds cold — Medium

| Time | Invoices | Duration |
|---|---|---|
| 07:38:37 | 1,303 | 64.1 s |
| 07:55:38 | 1,307 | 23.1 s |
| 08:00:49 | 2,324 | 58.3 s |
| 08:33:05 | 3,194 | 6.0 s |
| 09:11:00 | 3,195 | 23.6 s |

Cache hits are 30–90 ms
(`ShopInventory/Features/Invoices/Queries/GetPodUploadStatus/GetPodUploadStatusHandler.cs:100`), so the
cache works. But it is keyed on the exact `(FromDate, ToDate, scope)` with a ~15-minute TTL, and users
pick their own date ranges — so roughly half the requests in this window paid the cold price. Cost
grows with the range: the 08:33 run fired 105 SAP SQL queries.

**Fix.** Pre-warm the common ranges (last 7 days, last 14 days, month-to-date, per scope) on a Quartz
interval job, the way `CreditNoteProjectionSyncJob` already keeps its projection warm
(`ShopInventory/Configuration/QuartzConfiguration.cs:70`). Serve arbitrary ranges from the widest warm
snapshot and narrow in memory rather than rebuilding.

**Effort:** ~2 days. **Verify:** open the report on a fresh instance and confirm a first-load under 2s.

---

## 8. The full price catalog sync re-pulls everything, 3x a day, for no change — Medium

```
00:42:50 Full price catalog sync completed in 418927ms: 116 price lists, 19486 item prices, 5927 special prices
04:42:13 Full price catalog sync completed in 381742ms: 116 price lists, 19486 item prices, 5927 special prices
08:42:06 Full price catalog sync completed in 375321ms: 116 price lists, 19486 item prices, 5927 special prices
```

Byte-identical output across all three runs, with `removed 0 stale records` each time — 20 minutes of
sustained SAP load per day that changed nothing. It is also where 9 of the 15 SQL-budget warnings
originate.

Related, same shape: the credit-note projection sweep runs every 2 minutes; 225 of its 273 sweeps
reported an unchanged header count (51,219). Thirty new headers all day, at the cost of 273 SAP
round-trips.

**Fix.** Neither is urgent, and both should be handled the same way — ask SAP what changed rather than
re-reading everything:

- Price sync: filter on `UpdateDate`/`UpdateTS` per price list and skip lists whose watermark has not
  moved. Keep a weekly full reconciliation.
- Credit-note sweep: when the sweep changes nothing, log at `Debug`. That alone removes 450 lines/day.

**Effort:** ~2 days for the price sync; ~1 hour for the credit-note logging.

---

## 9. Reconciliation chases a dead order every two minutes, forever, at `[INF]` — Medium

276 times in 9 hours — every 2 minutes, all night, one SAP round-trip each:

```
00:01:51 Sales order reconciliation linked none of its 2 candidate(s); SAP holds no document under SO-20260817-0008, SO-20260817-0042.
…
09:11:54 Sales order reconciliation linked none of its 2 candidate(s); SAP holds no document under SO-20260817-0008, …
```

`SO-20260817-0042` was finally approved and posted at 09:06 (`DocNum=80745`) after a credit change.
`SO-20260817-0008` has been chased since the 17th — three days, roughly 2,000 futile SAP probes.

The code already predicts this exactly, at `SalesOrderService.cs:2073`:

> *"a sweep that links nothing every two minutes for hours means those orders need a person"*

…and then logs it at `Information`, where no one will ever see it.

**Fix.**

1. Escalate: track consecutive unresolved sweeps per order; past a threshold (say 30 — one hour),
   raise it once to `Warning` and into the Exception Centre so it reaches a human.
2. Back off: probe orders unresolved for more than a few hours on a slower cadence (every 30 min)
   rather than every 2 minutes. The candidate filter at `SalesOrderService.cs:1989` is the place.
3. Say why, once per order, not the same line 276 times.

**Effort:** ~1 day. **Verify:** an order stuck for an hour appears in the Exception Centre and its
probe cadence drops.

---

## 10. A 60-second failure with no log line at all — Medium

```
08:15:01 [INF] Handling GetStockInWarehousePagedQuery
08:16:01 [INF] Handled GetStockInWarehousePagedQuery
```

Exactly 60.000 s, then nothing. No result line, no error. The success path logs
`Retrieved page {Page} of stock items…`
(`ShopInventory/Features/Stock/Queries/GetStockInWarehousePaged/GetStockInWarehousePagedHandler.cs:43`),
and it is absent — so this went through the client-cancellation catch at line 58, which is the one
branch of six with no logger call:

```csharp
catch (TaskCanceledException)
{
    return Errors.Stock.SapError("Request was cancelled by client.");   // silent
}
```

A user waited a minute, got an error, and the API recorded nothing about it.

**Fix.** Log it at `Information` (a client abort is not a fault) with the warehouse, page and elapsed
time. Then sweep for the same shape elsewhere — `catch` blocks that return an error without logging.

**Effort:** ~1 hour for this site; ~half a day for the sweep.

---

## 11. Health alerting flaps on a self-healing blip — Low

```
08:11:21 [WRN] Health check … "Degraded" … 'Operational sync health is degraded (score 70): SAP connection is unstable.'
08:11:31 [WRN] System health is "Degraded" — sending failure alert
08:11:35       System health alert email sent to alerts@kefaloscheese.com
08:11:35       Created notification: System degraded for all
08:16:21       System health recovered to Healthy — sending all-clear
08:16:25       System health alert email sent to alerts@kefaloscheese.com
```

Two emails and two org-wide pushes for a five-minute SAP wobble that no user request failed on — the
price-list fallback absorbed all of it. `SystemFailureAlertJob` alerts on the *first* non-Healthy poll
(`ShopInventory/Services/Jobs/SystemFailureAlertJob.cs:96`).

**Fix.** Require `Degraded` to persist across two consecutive polls before alerting; keep `Unhealthy`
immediate. Suppress the all-clear when no alert was sent.

**Effort:** ~2 hours. **Test:** a single degraded poll followed by a healthy one sends nothing.

---

## 12. `FCM send failed for token cIqH8sOKRVCP…: null` — Low

`{Error}` is `response.Responses[i].Exception?.MessagingErrorCode`
(`ShopInventory/Services/PushNotificationService.cs:358`). It was null, so the message says nothing,
*and* the token was not pruned — pruning at line 354 only covers `Unregistered` and `InvalidArgument`.
A token failing for any other reason stays in the table forever and is retried on every send.

**Fix.** Log the exception's message and HTTP status alongside the code. Prune on `SenderIdMismatch`
and `ThirdPartyAuthError` too, and revoke any token that has failed on N consecutive sends regardless
of code.

**Effort:** ~2 hours.

---

## 13. Fifteen notifications to `Cashier` reached nobody — Low

```
Push notification 43932 reached no active devices for target Cashier   (x15)
```

Every sales order raises a Cashier notification and every one of them goes nowhere — nobody in that
role has a registered device. Either the role should not be a push target, or somebody's handset was
never registered. This is a configuration question for operations, not a code defect, but the log
should say it once rather than fifteen times.

**Fix.** Confirm with operations whether Cashier is meant to receive push. If yes, register the
device; if no, drop the role from the notification fan-out. Either way, log "no devices for role X"
at most once per hour.

**Effort:** ~1 hour once the question is answered.

---

## 14. 76% of the log is mechanical chatter — Low

| Category | Lines | Share |
|---|---:|---:|
| `Handling …` / `Handled …` pairs | 3,206 | 38% |
| `SQL query POD… returned N rows` | 1,047 | 13% |
| `API key authentication successful for: MainIntegration` | 718 | 9% |
| Reconciliation sweep (finding 9) | 552 | 7% |
| Credit-note sweep (finding 8) | 546 | 7% |
| Per-line UoM normalization | 276 | 3% |
| **Total** | **6,345** | **76%** |

The UoM line is per *line item* per posting attempt — order `SO-20260817-0042` alone produced 61 of
them, all saying the same thing (`'Each'` → `'EA'`). It should be one summary line per order.

**Fix.** Move `Handling`/`Handled`, the API-key success line and the per-row SQL result line to
`Debug`; collapse the UoM lines to one per order. This is not cosmetic — the signal in findings 9 and
10 is currently buried under it.

**Effort:** ~half a day.

---

## What Week 1 changed

Findings 1, 3, 4, 10 and 14, with tests. `dotnet build` is clean (0 warnings) and the suite is green
at 1,973 tests, 11 of them new.

**Level now means something.** A credit refusal is reported at Information without a stack, by
`SalesOrderService.LogFailedSapPost` — one helper, called from both catch blocks that used to hold
their own copy of the decision. On the day reviewed here that empties `[ERR]` entirely, which is the
point: the level is now available for faults.

**Every request records how it ended.** `LoggingBehavior` (both projects) replaces the
`Handling`/`Handled` pair with one line carrying the outcome and the duration — the returned
`ErrorOr` code and description, a client disconnect, or an escaping exception. A fast success drops
to Debug. This is what covers the sweep in finding 10: `scripts/find_silent_catches.py` finds 58
catch blocks that end a request unsuccessfully without logging, and reporting the outcome in the
pipeline covers all of them and every one written later. `GetStockInWarehousePagedHandler` also
logs locally, because the warehouse and page are worth having.

**Measured against this log**, with the API-key line, the per-query row count and the per-line UoM
normalization also moved to Debug (the last replaced by one summary line per order):

| | lines |
|---|---:|
| 2026-08-20 as recorded | 8,361 |
| moved to Debug | −5,249 |
| slow requests now named, with timings | +148 |
| UoM summaries, one per order instead of per line | +8 |
| **after** | **3,268** (61% smaller) |

The 148 added lines did not exist before in any form — the old pair gave a start and an end and left
the arithmetic to the reader.

**Concurrent refresh no longer logs anyone out.** `JwtSettings.RefreshTokenRotationGraceSeconds`
(default 60) lets a token that was rotated moments ago be honoured once more. The loser of the race
gets a pair of its own — only the successor's hash is stored, so there is nothing to replay and the
chain forks. Reuse outside the window is refused exactly as before, and the window is measured from
the original rotation so replaying every 59 seconds cannot keep sliding it forward.

**Browser addresses reach the API.** `ClientAuditHeaders` is one implementation of the stamping that
previously existed only on the sales-order path; the login, two-factor, passkey, registration and
refresh calls now use it. It stays per-call-site by necessity — `WebClientAuditContext` is scoped to
the Blazor circuit and a `DelegatingHandler` built by `IHttpClientFactory` resolves from its own
scope — but there is now one copy of it rather than one per caller.

### Not done, and deliberately

- **Chain revocation on late reuse.** A rotated token replayed long after the fact is a
  credential-theft signal, and the OAuth BCP says to revoke the whole chain. It is still only
  refused. That is a security-policy change that can log real users out, so it wants its own
  decision rather than riding along here.
- **The other 57 silent catches.** They are no longer silent — the pipeline reports their outcome —
  but roughly half return `Errors.X.InvalidOperation(ex.Message)` for validation refusals, where a
  local log line would recreate finding 1 rather than fix anything. The detector stays in `scripts/`
  so the question can be asked again per site.
- **No runtime run.** The API's startup jobs create SQLQuery objects in the live SAP company, so it
  was not started locally. The log-shape claims above are computed from this log's own request mix;
  the levels and messages are asserted directly against the real classes in the tests.

---

## What Week 2 changed

Findings 2, 5 and 9, with tests. Build clean (0 warnings), suite green at 1,993 tests, 20 of them new.

**A duplicate POD is no longer announced twice.** The duplicate guards inside
`UploadAttachmentAsync` were invisible to the caller — a reuse and a fresh store both return a valid
attachment — so `UploadPodHandler` fired its notification and push either way. That is what put two
bell entries and two pushes on invoice 2229934. `AttachmentUploadOutcome` now reports the reuse, and
the handler returns the attachment without announcing it again.

The intent guard added on 2026-08-06 was already in place on the day reviewed and still let three
duplicates through, because it was skipped whenever the caller supplied an external reference. The
reasoning was that a client minting its own reference is claiming those submissions are distinct —
but the handset mints one per *capture*, and a second capture is a different photo with a different
hash, so invoice 2232744 took a second POD two seconds later past every guard. The window now applies
either way, and `UploadPodCommand.IsAdditionalPage` is how a caller says it means a second page.

It stays at 15 seconds. The two duplicates 73 and 77 seconds apart are still stored, and from the
server they cannot be told apart from a genuine second page — widening the window before the handsets
send the flag would start silently discarding real ones. Widen it once they do.

**A slow SAP no longer holds a person's request.** `GetPricesByBusinessPartnerHandler` gets a
three-second budget on the live path, falling back to the local catalogue it already used as its
failure path. Without item codes the query lands on the whole-catalogue price list path, whose own
budget is 20 seconds — sized for the four-hourly sync — with an Items API fallback behind it that has
taken two minutes for one list. Three seconds is generous against a healthy SAP, where a price list
reads in 100–200 ms, so this keeps live pricing whenever it is actually available rather than trading
freshness away permanently.

The plan also proposed splitting `PriceListSqlRequestTimeoutSeconds` into interactive and batch
values. That turned out to be unnecessary: with this handler bounded, the only remaining callers of
the 20-second path are the two sync handlers, which is what the budget was sized for. No new setting.

**Reconciliation stops asking a question it has already answered.** The sweep ran every two minutes
over a seven-day lookback, so an order SAP never received was re-probed 720 times a day — 276 times
in this log, every one naming `SO-20260817-0008`, unlinked since the 17th. Each run now sweeps a
two-hour window, where a repair actually happens, and the full lookback on the half hour.
`SalesOrderReconciliationJob.IsFullSweep` buckets on the scheduled fire time so every node in the
cluster agrees which runs are full without sharing state.

Replayed against this log:

| | sweeps |
|---|---:|
| SAP probes as recorded | 276 |
| SAP probes after the change | 19 |
| avoided | 257 (93%) |

An order still unlinked an hour after it was created is now reported at **Warning**, naming it, with
the reason it will not resolve on its own. A SAP create that committed is visible within moments, so
past that point further probing cannot change the answer — it needs a person. Combined with the
half-hourly cadence that names a genuinely stuck order about 48 times a day instead of writing 720
Information lines nobody reads.

### Not done, and deliberately

- **An Exception Centre source for stuck orders.** The nicer home for finding 9's escalation, since
  that is the screen people actually watch. It is a query-side aggregator rather than something you
  raise into, so a new source means a loader, DTO mapping and retry semantics across a 1,241-line
  handler — a bigger change than the rest of Week 2 combined, and worth its own pass. The Warning
  makes the condition visible in the meantime.
- **No runtime run**, for the same reason as Week 1: the API's startup jobs create SQLQuery objects
  in the live SAP company. The probe and log-volume figures above are replayed from this log's own
  data; behaviour is asserted against the real classes in the tests.

---

## What Week 3 changed

Findings 7 and 6, and the safe half of 8. Build clean (0 warnings), suite green at 2,012 tests, 19
of them new. Route catalogues re-checked and clean: 454 routes, every one documented.

**The POD report is rebuilt before somebody needs it, not while they wait.** In this log, 6 of 9
requests were rebuilds rather than cache hits, and users spent **182 seconds** in total waiting on
them — the worst single wait 64 seconds.

The trap here is warming too much: four preset ranges on a ten-minute timer is several hundred SAP
rebuilds a day for reports nobody opened, which is worse than the problem. `PodReportWarmSet` records
what was actually asked for and `PodReportWarmJob` rebuilds only those, five minutes ahead of expiry.
Nothing runs overnight; during the day it follows the two or three shapes in play. It does not reduce
the SAP work much — it moves it off the request path, which is the part a person feels.

The set is held in memory rather than as a column on the cache table. Losing it on a restart costs
one cold build per shape to relearn, which is what would have happened anyway, and it keeps a report
read from writing to the database.

**An approver can see the credit room before clicking.** The sweep behind credit control already
measured every account and every consolidated group and then threw away the ones inside their limit;
it now keeps them, and `GET /api/credit-control/headroom` answers for the customers on a screen from
that same cached sweep. The approval dialog reads it when it opens.

For a consolidated account it reports the **parent's** limit, because that is the one the order is
actually refused on — FOO030 was refused on FOO025's group limit, not its own, and a payment against
FOO030 would not have moved it. `hasLimit: false` is reported distinctly from zero room: those are
opposite answers and a screen that showed 0.00 for both would send a rep chasing a payment that was
never needed.

Refactoring the two readers onto one cached sweep broke an existing test, correctly: `GeneratedAt`
was being stamped when the DTO was built rather than when SAP was read, so a cached answer would have
claimed to be current. The timestamp is now cached with the sweep, and the dialog says which moment
the balances belong to.

**The credit-note sweep says something only when something changed.** It ran 273 times in this log
and reported the same header count 225 times over — thirty new credit notes in a whole day, announced
273 times. It now logs at Information only on a change, with the delta, and the per-poll SAP fetch
line drops to Debug. About 500 lines a day.

### Not done, and deliberately

- **The incremental price sync** — the other half of finding 8, and the larger one. It needs a
  per-price watermark from SAP, and there is none: `UpdateDate` appears in this codebase only on
  documents, and B1 stores item prices in `ITM1`, which carries no update stamp of its own. The
  workable key would be `OITM.UpdateDate`, on the assumption that changing a price stamps the item —
  which is not guaranteed, because a price edited through the Price Lists window writes `ITM1`
  without necessarily touching `OITM`. Verifying that means changing a price in the production
  company and watching what moves. Getting it wrong means prices silently stop updating, which is
  worse than a sync that costs seven minutes three times a day. It needs that check first, not a
  guess from here.
- **The approval dialog was not exercised at runtime.** The Razor compiles, the bindings type-check,
  and no new Web services were introduced, so the wiring risk is small — but nobody has looked at it.
  It reuses only existing `sox-note` classes and adds no CSS. Drive it with `verify-shopinventory-web`
  against an environment you are happy to point at SAP; the headroom figures need a live sweep to
  mean anything.
- **Findings 11, 12 and 13** remain as backlog, unchanged.

---

## What the backlog changed

Findings 11, 12 and 13, with tests and a negative control on each. Build clean (0 warnings), suite
green at 2,034 tests, 22 of them new.

**A five-minute wobble no longer pages everyone.** `SystemHealthAlertSettings.DegradedConfirmations`
(default 2) makes a Degraded reading hold across two consecutive polls before it is alerted on.
Unhealthy is exempt — a hard failure is not a blip and waiting a poll costs real minutes. Replayed
against this log's own timeline: Degraded observed at 08:11, healthy again by 08:16, polls five
minutes apart, so it never earns an alert — and because no alert went out, the recovery has nothing
to announce either. **Four messages become none:** two emails and two org-wide pushes for something
that failed no user request.

The count resets on any healthy poll, so a condition that appears and clears repeatedly can never
accumulate its way to a confirmation it did not earn. The decision is a pure function the job and its
tests share, rather than a rule the tests re-derive and agree with themselves about.

**The FCM diagnostic says something.** `FCM send failed for token cIqH8sOKRVCP…: null` was the
messaging error code alone, which is null for anything FCM did not classify as a messaging fault. The
line now carries the general error code and the message. Pruning gained the two other permanent
registration failures — `SenderIdMismatch` (a token minted for a different Firebase project) and
`ThirdPartyAuthError` (an APNs credential bound to the token). Unclassified failures are deliberately
*not* treated as permanent: revoking on a transport hiccup silently stops a working handset receiving
anything.

**A standing misconfiguration is stated once, not per event.** Sixteen "reached no active devices"
lines, fifteen of them Cashier, each announcing the same fact: no device is registered against that
role. Now at most one per target per hour, carrying a count of what it stands for — **16 lines become
3** across this log. Per target, because Cashier having nobody says nothing about whether a named
driver does.

### One question for operations

Sales-order notifications are raised for exactly two roles, `Admin` and `Cashier`
(`NotificationService.CreateSalesOrderRoleNotificationIfMissingAsync`), and the Cashier push found no
device every single time on 2026-08-20. Worth knowing that **nothing was lost**: the notification is
stored and readable in the bell by anyone in that role, and only the push fan-out had nowhere to go.
So this may be entirely correct — if cashiers work from the web app rather than a handset, there is
nothing to fix and the throttled line is the whole answer. If they are meant to have the app, a device
needs registering. That is a question about how the depot works, not about the code, so it has been
left as it is rather than guessed at.

---

## Suggested sequence

**Week 1 — stop the bleeding, restore the signal.**
Findings 1, 10, 14 (logging levels and the silent catch), then 3 and 4 (auth correctness).
Nothing else can be triaged reliably until `[ERR]` means something again.

**Week 2 — the user-visible defects.**
Finding 2 (duplicate PODs — the notification half first), then 5 (the 20s price stall) and 9
(reconciliation escalation).

**Week 3 — the slow paths.**
Findings 7 (POD report pre-warm), 6 (credit headroom before approval), 8 (incremental sync).

**Backlog.** Findings 11, 12, 13.

---

## Appendix A — the SAP query-name churn is working as designed

The log contains 114 distinct SAP SQL query names (`PODCRA_64EFDA2DB0B1`, `POD_SO_8372419AE2A3`, …).
Given the known history of leaked `OUQR` rows, this looks alarming. It is not.

Checked directly: **all 105 names from the 08:33 report run were reused verbatim at 09:11** (zero new),
and all 41 names from the 07:38 run reappear in both later runs. New names appeared only when the
report's invoice range widened — 40 at 07:38 (1,303 invoices), 21 more at 08:01 (2,324), 43 more at
08:33 (3,194) — and then stopped.

That is exactly what `SqlIdRangeCover` is for
(`ShopInventory/Features/Invoices/Queries/GetPodUploadStatus/GetPodUploadStatusHandler.cs:1165`):
querying id *ranges* rather than exact doc entries, so a range recurs across requests and SAP reuses
one query object. Growth tracks invoice-id growth, not request count. **No action needed** — recorded
here so the next reader does not re-open it.
