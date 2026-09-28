# SAP outages

How the API notices that SAP is down, what it does while SAP is down, and what happens when SAP comes
back. This covers background posting. Capturing sales without SAP is a separate piece of work.

## Detection

`SapAvailabilityProbeJob` runs every 30 seconds on one node (clustered Quartz). Each run asks SAP for
one warehouse code on the shared session (`ISAPServiceLayerClient.PingAsync`), with a 20-second budget
and interactive priority, so a busy slot pool does not look like an outage.

| Event | Rule |
|---|---|
| Outage declared | 3 failed probes in a row (about 90 s). The outage is dated from the **first** failed probe. |
| Outage ended | 2 good probes in a row. The end is dated to the **first** good probe. |
| SAP switched off in Settings | An outage opens at once, with cause `SwitchedOff`. Switching SAP back on does not end it; the next good probes do. |

Outages are rows in `SapOutages`. At most one row is open (`EndedAtUtc` null). Every node re-reads the
open row every 15 seconds (`SapAvailability`); the node that writes a change applies it at once.

The per-node circuit breaker still exists and is unchanged. It protects one node from SAP for 30 seconds.
The outage record is the cluster's view and is what the background passes use.

## While SAP is down

Background passes call `SapCircuitBreakerState.ShouldHoldBackWork` and skip their round when the
connection is switched off, an outage is open, or this node's circuit is open:

- Till and vending posting (`DesktopSalePostingService`)
- Van end-of-day posting (`VanSalesEndOfDayPostingService`)
- Queued online van invoices (`PostQueuedVanInvoicesHandler`)
- Daily incoming payments (`DailyIncomingPaymentService`)
- Desktop credit memos (`DesktopCreditSapSweep`). This pass still runs, because returning a credit's
  units to the stock ledger needs nothing from SAP. It only stops short of the memo.
- Queued inventory transfers and queued incoming payments (`InventoryTransferPostingJob`,
  `IncomingPaymentPostingJob`)

Manual "Post to SAP" buttons are **not** held back by a declared outage, only by the node's own
circuit. A person can still try, and the probe can still reach SAP.

### Attempts

A failure caused by SAP being unreachable does not spend a document's attempts:

- **Sales, till and van:** unchanged. Transient failures already did not count.
- **Credit memos:** transient failures no longer count. Before, an outage used up all 6 attempts in
  6 minutes.
- **Queued transfers and payments:** a failure that proves nothing was sent gives the attempt back and
  retries after a minute. That covers SAP switched off, the circuit open, and a login that failed before
  the post. A timeout still counts, because neither document carries a reference SAP could be asked
  about, and replaying an unknown outcome without limit is how one transfer becomes two.

A gateway error page (502/503/504 with no SAP error body) in answer to an invoice post is now an
unknown outcome, not a refusal. It is retried, and the sale is looked up in SAP (`U_Van_saleorder`)
before it is sent again.

## After SAP comes back

The posting windows would otherwise strand sales held up by a long outage:

| Pass | Own window |
|---|---|
| Till posting | 3 days |
| Credit memos | 3 days |
| Van posting | 7 days |
| Daily payment | 7 days |

Each window reaches back to the day before the start of any outage that was still going on when the
window opened, or is still open (`SapOutageReach`). The furthest it reaches is
`SapAvailability:MaxLookbackExtensionDays` (30). The Exception Center uses the same van window, so it
does not report sales as stranded that the pass is about to post.

## Shop stock while SAP is down

A shop sells from its daily stock snapshot, fetched from SAP at 07:00. When today's snapshot is not
finished, the shop sells from yesterday's (`StockSnapshotInForce`). That is one day only, so that a fetch
failing for any other reason stops the tills on the second morning and gets noticed.

**During a recorded outage the shop may reach further back.** It can use its last finished snapshot
within `DailyStock:OutageCarryOverDays` (default 7), provided one outage:
- was already under way at the first missed morning's fetch (with an hour of slack for how outages are
  dated), and
- was still going on at this morning's fetch.

The rows the tills sell from are the ones they have been moving all along. Every sale is journalled under
today, as for any carried-over day. Vans are never carried over.

**Retry.** The snapshot job's 10-minute retry trigger (`daily-stock-snapshot-unbatched-retry`) also
fetches again any shop whose snapshot for the day failed or never started. It is skipped while SAP is
held back, so the shop moves onto today's figures within about ten minutes of SAP returning. That fetch
takes off every till sale SAP has not had yet, whatever day it was made, so nothing sold during the
outage goes back on the shelf.

## Van sales while SAP is down

An online van sale reserves its stock before the receipt is signed, and the reservation used to read
SAP for the van's stock. During an outage every van sale failed there. The reservation now checks a
van against this system's own count in either of two cases:

- **SAP work is held back** (`ShouldHoldBackWork`): switched off, a declared outage, or this node's
  circuit open.
- **SAP did not answer this request's stock read**: a timeout, a dropped connection or an open circuit
  (`SapFailureClassifier.IsTransient`), when every line that went to SAP is for a van. The probe only
  pings, and a Service Layer that answers a ping can leave the stock query hanging for its whole
  60-second budget, so no outage is ever declared. Until 2026-09-28 every van sale then failed with a
  504 ("a downstream service did not respond in time") with nothing signed. A request that also names
  a depot or shop still fails, since their stock is only in SAP. A refusal from SAP is SAP answering,
  and is not rescued. The API logs `SAP did not answer the stock read for van(s) …` each time.

Either way:

- **The figure.** The van's opening count, plus loads, less today's sales. It is the same arithmetic as
  `GET /api/vansales/stock/position` (`VanStockPosition`), with one difference: online sales whose
  reservation is still holding stock are left out, because live holds are subtracted separately.
- **A shortfall is refused** (`INSUFFICIENT_STOCK`), as at a till.
- **A van with no opening count for the day is refused** (`STOCK_NOT_COUNTED`), telling the rep to
  count the van first.
- **Nothing on the van's lines asks SAP.** UoM conversion is taken as one-to-one, which is what it
  answers when SAP cannot be reached. Batches the handset named are kept; the rest are left empty.
- **Only vans** (`VanWarehouses`) are checked locally. Every other warehouse still reads SAP.

The sale is then signed and queued as usual, and `PostQueuedVanInvoices` posts it once SAP is back.
Just before the invoice goes out, any batch-managed line with no batches gets them chosen FEFO
(`AllocateMissingBatchesAsync`), with the reservation's own hold set aside. A reservation made while SAP
was up already has its batches and is not touched. If allocation fails, an unreadable warehouse returns
the reservation to Pending for the queue to retry, and a real shortfall fails it for a person, as a SAP
refusal would.

## Decided, and deliberately not built (2026-09-28)

- **Van loading stays blocked while SAP is down.** The handset's `inventory/request` and
  `inventory/confirm`, and the till's transfer requests, go straight to SAP and fail during an outage.
  Pre-load vans generously when an outage is expected. Recording loads locally and posting them later
  was considered and not built, because the outages seen so far have lasted hours.
- **Month-end keeps the original date.** A sale that reaches SAP after its posting period has closed is
  refused (`SapPostingPeriodException`), parks after its attempts, and is grouped in the Exception
  Center as "posting period closed". Reopen the period, then use the Exception Center's batch retry.
  Nothing re-dates a sale, so the SAP invoice keeps the date of its fiscal receipt.
- **The desktop invoice and transfer routes** (`POST api/DesktopIntegration/invoices`,
  `invoices/queued`, `transfers`, `transfers/queued`) have no caller in the Web app, the till or the van
  handset, so they get no outage handling.

## Settings

`SapAvailability` section. All keys are optional; the defaults are below.

```json
"SapAvailability": {
  "Enabled": true,
  "ProbeIntervalSeconds": 30,
  "ProbeTimeoutSeconds": 20,
  "FailuresToDeclare": 3,
  "SuccessesToEnd": 2,
  "MaxLookbackExtensionDays": 30
}
```

The probe is scheduled only when `SAP:Enabled` and `SapAvailability:Enabled` are both true. Removing a
job from code does not remove it from the clustered store. To stop the probe, set `Enabled` to false
and delete its trigger from Quartz.

## Checking

- `GET /api/health`: the `sap` check reads "SAP is unavailable (declared outage)", with `outageId`,
  `sinceUtc` and `cause` in its data. (`/health/dependencies` answers with the overall status only.)
- Outage history:
  ```sql
  SELECT "Id", "Cause", "StartedAtUtc", "DeclaredAtUtc", "EndedAtUtc", "FailedProbes", "LastError"
  FROM "SapOutages" ORDER BY "Id" DESC LIMIT 20;
  ```
- The API log line `SAP availability is now …` marks each change on each node.
