# Customer statements from a Postgres journal copy — plan

Status: **step 0 measured on 6 Nov 2026; phase 1 (1a–1c) built on 6 Nov, not
yet deployed.** The measurement changed the order. Statements are not slow on
average. Instead, one SAP read occasionally hangs until SAP's 5-minute limit,
and the customer waits all of it. Phase 1 deals with that directly. Phase 2,
the journal copy, is on hold until phase 1 has been measured again.

## The problem

Every customer statement is built live from SAP. Postgres stores none of it.
`GetCustomerStatementHandler` makes these reads for a customer with *n* linked
accounts:

| Read | SAP calls |
|---|---|
| Business partner (name, email, currency, terms group) | 1 |
| Payment terms | 1 |
| Opening balance (`STMT_OPENING_BALANCE`, `SUM` over all history before the period) | *n* |
| Ledger rows in the period (`STMT_LEDGER_ROWS`, paged 500 rows a call) | *n* or more |
| Open items for aging (`STMT_OPEN_ITEMS`) | *n* |

That is **3*n* + 2 SAP calls**, two at a time (`MaxConcurrentSapReads`). They go
through the same six process-wide SAP slots as the shops, vans and back office.
Every one of those calls waits up to the SAP client's 5-minute timeout
(`SAP:RequestTimeoutMinutes`), which is also how long the portal waits. So one
stuck read is enough to lose the whole statement. Step 0 shows that is what
happens.

The only cache is `StatementBuildCache`. It is in memory, holds a finished
statement for **5 minutes**, and only answers the same customer, accounts and
**exact** date range. Any other range, any request after 5 minutes, and every
request after a deploy or app-pool restart goes back to SAP for everything.

Most of what a statement shows never changes once SAP has posted it. An
invoice from March is the same invoice every time it is read. Re-reading it
from SAP on every request is the waste this plan removes.

## Step 0 — measure (done 6 Nov 2026)

`scripts/Measure-StatementBuildTimes.ps1` is read-only. Run it on the API box
in an elevated Windows PowerShell 5.1:

```powershell
powershell -ExecutionPolicy Bypass -File .\Measure-StatementBuildTimes.ps1 -Days 31
```

It reads the API and Web logs under every IIS slot. Sections 6 and 7 list card
codes, so keep those out of PRs (the repo is public).

### What it showed

| Measure | Result |
|---|---|
| Builds started / finished | 12 / 8: **4 never finished** |
| Finished builds | median 0.3 s, slowest 8.0 s, all single-account |
| Transactions read (`ledger rows`) | median 0.9 s; **at least 2 of 12 hit SAP's 300 s timeout** |
| Aging read (`open items`) | median 0.2 s; 2 of 12 took 72–79 s |
| Opening balance | median 0.1 s, slowest 8.6 s |
| Business partner | median 0.0 s, slowest 8.7 s |
| Payment terms | 0.0 s every time |
| Portal requests that gave up at 300 s | **6** |
| Served from the 5-minute cache | 2 |

What that means:

- **The problem is stuck reads, not slow ones.** Every read normally answers
  in under a second. Occasionally one does not answer at all until SAP's
  5-minute timeout kills it.
- **A stuck transactions read most likely explains each unfinished build.**
  The slowest finished build took 8 s. A build whose only slow read was a
  79-second aging read would still have finished, so each of the four that
  didn't must have had a read stuck for the full 5 minutes. The first run of
  the script could not show which read or when; the script now lists both
  (section 7).
- **That is the spinner in the screenshot.** The portal waits the same
  5 minutes, then gives up. It did so 6 times, against 12 builds.
- **Traffic is light.** The 8 finished builds all fell on 4–6 Nov. The new
  version of the script prints the time span the logs actually covered.

So caching more of the statement is not the first fix. Either the customer
stops waiting 5 minutes for a read that normally takes a second (phase 1, a
day's work), or the read is not made at all (phase 2, about a week plus an
overnight load from SAP). Phase 1 first, then measure again.

## Phase 1 — stop waiting for a stuck read

**1a. A 30-second budget and one retry for each statement SQL read.** The
stock reads already have this machinery: `ExecuteStockSqlQueryWithBudgetAsync`
in `SAPServiceLayerClient` cancels one read when its budget runs out, counts
that as a circuit-breaker failure, and gives the SAP slot back. Make it take
the budget and a label, and use it for the three statement queries with 30 s
per attempt and one retry. The worst case becomes about a minute instead of
five.

- 30 s is generous: the reads normally take 0.1–0.9 s, and the slowest read
  in a finished build took 8.6 s.
- **Not known:** whether a retry after a stuck read succeeds. SAP has six
  Service Layer nodes behind a load balancer and the app does not pin one, so
  a retry probably reaches a different node. Nobody has measured it; the
  second measurement will.
- If the transactions or opening balance read still fails after its retry,
  the statement fails at once with "We couldn't reach the accounting system.
  Please try again." That replaces a 5-minute spinner.
- If the aging read fails, the statement is shown without aging. It already
  does this.

**1b. Say aging is unavailable instead of printing zero.** When the aging read
fails today, the handler builds aging from no rows. The page then prints
"0.00 outstanding" for a customer who owes money. Add `AgingAvailable` to
`CustomerStatementResponseDto` and to the Web's hand-written copy of it. The
page then shows "Aging couldn't be loaded. Generate the statement again to
retry." in that block, and the PDF leaves the aging table out. Ship this with
1a, because a 30-second budget makes this path more common.

**1c. Log what actually timed out.** The handler logs "exceeded its 10-minute
budget" for any cancellation the caller did not ask for. That includes one SAP
read hitting the 5-minute client timeout, so the message names the wrong limit.
Log which read timed out and after how long.

**1d. Optional: cache the business partner for 10 minutes.** Its slowest read
was 8.7 s. Payment terms took 0.0 s every time, so leave them.

### What was built (6 Nov 2026)

1a, 1b and 1c, with three details the plan above did not spell out:

- **The budget is not a circuit-breaker failure.** Stock reads count theirs;
  price-list reads deliberately do not. A stuck statement read is the
  price-list case: counting it would let a few statements open the breaker and
  refuse every posting.
- **A statement without its aging is not cached.** It is still shown, but it
  is not kept in the 5-minute cache, so "generate again" really does retry the
  aging read.
- **The PDF prints a note** in place of the aging table, rather than leaving
  the section out without saying why.

The settings are `SAP:StatementSqlRequestTimeoutSeconds` (30) and
`SAP:StatementSqlMaxAttempts` (2). The API log now says
`… got no answer from SAP within 30s (attempt 1 of 2); retrying` and
`… giving up`; the script counts both. 1d was not built.

### Measure again

Two weeks after phase 1 ships, run the script again:

| Result | Next step |
|---|---|
| No unfinished builds, no portal give-ups | Done. Phase 2 stays on hold. |
| Reads still fail after their retry | Phase 2: stop making the transactions and opening balance reads |
| Aging is the read that fails | Load aging after the transactions (below), then phase 3 if needed |

**If needed: load aging after the transactions.** Add
`GET api/statement/{cardCode}/aging` with the same parameters. The page shows
the statement first and fills in aging when it arrives. The PDF waits for
both, and the 5-minute cache serves the PDF straight after viewing.

## Where each part of the statement would come from after phase 2

| Part | Today | After phase 2 |
|---|---|---|
| Customer name, email, currency | SAP, every build | SAP, cached in memory 10 min (1d) |
| Payment terms | SAP, every build | Unchanged (0.0 s measured) |
| Opening balance | SAP, one `SUM` per account | Postgres |
| Transactions in the period | SAP, one paged read per account | Postgres |
| Postings since the last sync | (part of the above) | One SAP read covering every customer, skipped if the copy synced in the last 30 s |
| Aging (open items) | SAP, one read per account | Unchanged, under phase 1's budget |

SAP calls per statement drop from **3*n* + 2 to *n* + 1**.

## Phase 2 — the journal copy (on hold)

Build this only if the measurement after phase 1 still shows statements lost
to stuck transactions or opening balance reads. The copy removes those two
reads altogether, so a stuck one can no longer cost a statement.

### What is copied

The API's Postgres database gets a new table, `SapJournalLines`. It holds every
**business-partner line** of every SAP journal entry. That means customers and
suppliers, never G/L-only lines. It copies the same columns `STMT_LEDGER_ROWS`
reads, plus the keys:

| Column | From | Notes |
|---|---|---|
| `TransId`, `LineId` | `JDT1."TransId"`, `"Line_ID"` | Primary key |
| `CardCode` | `JDT1."ShortName"` | Indexed with `RefDate` |
| `Account` | `JDT1."Account"` | The control account |
| `RefDate` | `OJDT."RefDate"` | Posting date |
| `Number`, `TransType`, `BaseRef`, `Memo`, `CreatedBy` | `OJDT` | As the statement reads them today |
| `ContraAct`, `LineMemo` | `JDT1` | |
| `Debit`, `Credit` | `JDT1` | Local currency, what the statement sums today |
| `FCDebit`, `FCCredit`, `FCCurrency` | `JDT1` | |
| `SYSDeb`, `SYSCred` | `JDT1` | System currency. The statement does not use these. They are copied so the unexplained gap in one account's opening balance can be investigated in Postgres instead of by repeated SAP probes |
| `SyncedAtUtc` | — | |

The filter for business-partner lines is
`(T1."ShortName" > T1."Account" OR T1."Account" > T1."ShortName")`. On a G/L
line SAP sets `ShortName` equal to `Account`. This is the same
column-against-column form `STMT_OPEN_ITEMS` already uses, because SAP's query
validator accepts it.

Size: one row per invoice, receipt, credit note or journal line that touches a
customer or supplier. Two invoices two days apart in January 2026 were about
750 numbers apart, roughly 370 invoices a day, so expect a few hundred
thousand rows a year. That is one data point, not a count. Either way it is
small for Postgres. The real cost is the SAP time for the first load (below).
The first backfill chunk logs rows per second, which turns this guess into a
number.

### Why a copy stays correct

A posted journal line's amounts, accounts, posting date and business partner
**never change in SAP**. Cancelling a document or reversing a journal entry
posts a *new* journal entry with a new `TransId`. So a line copied once stays
correct, and syncing only means fetching lines with a `TransId` above the last
one seen.

What *can* change on an existing line:

- **Reconciliation fields** (`BalDueDeb`, `BalDueCred`) change every time a
  payment is matched to an invoice. They are not copied, which is why aging
  stays a live SAP read (phase 3 is the option to change that).
- **Text and due dates** (`Memo`, `LineMemo`, references, line `DueDate`) can be
  edited on manual journal entries. The nightly check re-reads the last 7 days
  in full, so an edit shows within a day. An older edit only changes a
  description, never an amount.

This app never writes `JournalEntries` to SAP, so none of these changes come
from here.

### SAP queries (three fixed codes, no OUQR growth)

| Code | Reads |
|---|---|
| `JRNL_BP_LINES_RANGE` | Business-partner lines with `T0."TransId" >= :fromTransId AND T0."TransId" <= :toTransId`, ordered by `TransId`, `Line_ID`. A bounded range keeps every call to a few pages, the same shape the POD report's `DocEntry` ranges already use |
| `JRNL_MAX_TRANSID` | `SELECT MAX(T0."TransId") FROM "OJDT" T0` |
| `JRNL_BP_TOTALS` | Per `ShortName`: `COUNT(*)`, `SUM("Debit")`, `SUM("Credit")` for business-partner lines with `TransId <= :maxTransId`, grouped by `ShortName` |

Each is a parameterised statement under a fixed code, like the three statement
queries today. Together they add three rows to OUQR, permanently and once.

### Sync

The sync follows `CreditNoteProjectionSyncService`: a checkpoint in
`SystemConfigs`, status in `CacheSyncStates`, a Quartz job with
`[DisallowConcurrentExecution]` in the clustered store, and background SAP
priority so it never uses the two slots kept for people.

1. **Backfill.** Read `JRNL_MAX_TRANSID` once, then walk `TransId` from 1 to
   that number in chunks of 5,000. Save the checkpoint after each chunk, so a
   restart resumes where it stopped. Only run between 19:00 and 06:00 CAT so
   the backfill never competes with trading hours. Log rows and seconds per
   chunk.
2. **Every 2 minutes.** Read `TransId` from (last seen − 500) to the current
   maximum, and insert or update on (`TransId`, `LineId`). The 500-wide overlap
   covers a posting that took a lower number but committed after a higher one.
   Re-reading a line already copied changes nothing.
3. **Nightly check, 02:30 CAT.** Run `JRNL_BP_TOTALS` and compare count, debits
   and credits per card code against the same `GROUP BY` in Postgres. Re-read
   in full every card code that differs, and log a warning naming the
   difference. Also re-read the last 7 days in full to pick up text edits.
   A mismatch after the re-read is recorded as the sync's `LastError`, which
   makes the copy stale (below) until someone looks.

The nightly check is what proves the copy matches SAP every day, not only on
the day it was built. Its warnings also say whether the 500-wide overlap is
wide enough.

### Reading it in the statement

`GetCustomerStatementHandler` reads the copy only when **all** of these hold:

- `JournalProjection:UseForStatements` is on;
- the backfill has finished;
- the last successful sync is under 10 minutes old, and no error has been
  recorded since it.

Otherwise it builds from SAP exactly as it does today. That code stays as the
fallback, so the worst case is today's behaviour, never a wrong statement.

When the copy is used:

1. **Top-up.** If the last sync was more than 30 seconds ago, run the 2-minute
   sync step once, at interactive priority, with a 15-second budget. Requests
   in the same process share one top-up. If it fails or runs out of time, build
   from the copy anyway and say how fresh it is (next point).
2. **Build.** The opening balance is a `SUM` over `SapJournalLines` before
   `fromDate`. The transactions are the rows between `fromDate` and `toDate`
   for all the customer's card codes, in one query (no per-account fan-out).
   Then the same running-balance and totals code as today.
3. **Freshness stamp.** Add `PostingsAsAtUtc` to `CustomerStatementResponseDto`
   and to the Web's hand-written mirror of it (nullable on both sides). The
   page prints "Includes postings up to 06:48" under the generated time when
   the top-up did not run to completion.

### Proving it before switching on

`JournalProjection:Enabled` fills the copy. `UseForStatements` decides whether
statements read it, and stays off at first. This is the same split as
`CreditNoteSync:Enabled` and `UseForPodReports`.

While `UseForStatements` is off and the copy is ready, every statement still
builds from SAP and then also builds its ledger from the copy. It compares the
two (opening balance, line count, total debits, total credits, closing
balance) and logs a warning on any difference. This costs milliseconds and
answers "does the copy give the same statement as SAP?" on real traffic. Turn
`UseForStatements` on after a week with no difference.

Tests (unit, no SAP):

- The sync resumes from its checkpoint, and a re-read overlap inserts nothing.
- The same SAP rows produce an identical `CustomerStatementResponseDto`
  whether they come through the SAP path or the copy path. This is a golden
  comparison over a fixed set of rows, including a reversal pair and a
  foreign-currency line.
- A nightly mismatch triggers a full re-read of that card code only.
- The handler falls back to SAP when the copy is stale, mid-backfill or
  flagged off.

### Settings

```json
"JournalProjection": {
  "Enabled": false,
  "UseForStatements": false,
  "PollIntervalMinutes": 2,
  "StaleAfterMinutes": 10,
  "TopUpAfterSeconds": 30,
  "TopUpBudgetSeconds": 15,
  "BackfillChunkSize": 5000,
  "BackfillWindowCat": "19:00-06:00",
  "OverlapTransIds": 500,
  "NightlyCheckAtCat": "02:30",
  "RecentRereadDays": 7
}
```

## Phase 3 — aging from Postgres (only if needed)

Only worth doing if aging keeps failing after phase 1 *and* loading it after
the transactions is not good enough.

Aging needs each line's open balance, which changes with every reconciliation.
SAP records reconciliations in `OITR`/`ITR1` with an increasing `ReconNum`, and
`ITR1` names the journal lines each reconciliation touched. Syncing new
reconciliations the same way as journal lines would tell exactly which copied
lines need their balance re-read.

**Unverified:** whether SQLQueries may read `OITR`/`ITR1` (it refuses some
tables, such as `OUQR`), and how a cancelled reconciliation appears. Probe both
before committing to this phase.

## What this costs

- **SAP during the backfill.** One background slot of four, out of hours, in
  bounded chunks. Total time is unknown until the first chunk reports.
- **SAP afterwards.** One small read every 2 minutes, and one grouped read and a
  7-day re-read nightly. Statements need far fewer reads than they do today.
- **OUQR.** Three fixed query objects, once.
- **Two copies of the truth.** Contained by the shadow comparison, the nightly
  check, and the automatic fall-back to SAP whenever the copy is stale.
- **Deploys.** The copy survives restarts, unlike the 5-minute cache, so the
  first statement after the 19:30 deploy is no longer cold.

## Open questions

- Does `MAX()` pass SAP's query validator? `SUM`, `COUNT` and `GROUP BY` are
  known to pass. If not, read the highest `JdtNum` through OData
  `JournalEntries?$select=JdtNum&$orderby=JdtNum desc&$top=1`.
- How many business-partner journal lines exist, and how fast do range reads
  page? The first backfill chunk answers both.
- Should the G/L account ledger (`GetGLAccountLedgerHandler`, the same
  `OJDT`/`JDT1` read keyed on account) use the copy too? That needs G/L-only
  lines as well, which is many times the volume. Leave it out of this plan.
