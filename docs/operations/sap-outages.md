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
