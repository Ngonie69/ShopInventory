# SAP Service Layer interaction audit

Date: 2026-07-27
Scope: `ShopInventory` API — `SAPServiceLayerClient` (13,618 lines), the three SAP `DelegatingHandler`s,
and every caller of `ISAPServiceLayerClient` in `Services/`, `Features/`, `Controllers/`.

The findings are ordered by expected impact. Sections 1–3 are *multipliers*: they inflate the cost of
every SAP call in the system, so they should be fixed before any individual call site.

---

## 1. Every SAP request costs 2–3 extra Postgres round-trips, inside the concurrency slot — FIXED

`Middleware/SAPRequestLoggingHandler.cs:46` runs on **both** SAP HTTP clients
(`Program.cs:607`, `Program.cs:661`) and, in the `finally` of every request:

1. creates a DI scope and resolves `ApplicationDbContext`,
2. `INSERT` + `SaveChangesAsync`,
3. `SELECT id … ORDER BY CheckedAt DESC OFFSET 100`,
4. `DELETE … WHERE id IN (…)` whenever that returns rows — which, past the first 100 requests, is
   *every* request, because the table is capped at 100 rows.

Two things make this worse than it looks:

- It is `await`ed before `SendAsync` returns, so the DB latency is added directly to every SAP call's
  measured duration.
- It is registered **last**, so it is the innermost handler — the DB work happens while the request
  still holds a `SAPConcurrencyHandler` semaphore slot (only 6 exist process-wide) and a pooled TCP
  connection to SAP. A slow Postgres write throttles SAP throughput for the whole process.

**Fixed.** `SAPRequestLoggingHandler` now only calls `SapRequestLogQueue.TryEnqueue` — a bounded,
drop-oldest `Channel` write that cannot block or fail. `SapRequestLogWriter` (a `BackgroundService`)
drains it, batching a 200 ms linger window into one insert, and trims once per 100 rows written
rather than once per row, using a single range delete instead of a materialised id list. Retention
is unchanged at 100 rows, which is more than the readers in `SyncStatusService` ask for.

Net effect per SAP call: 3 Postgres round-trips inside the concurrency slot → 0.

---

## 2. Every SQL-backed call pays a redundant existence probe — FIXED

`EnsureSqlQueryAsync` (`SAPServiceLayerClient.cs:3240`) unconditionally calls
`TryGetSqlQueryTextAsync` — a `GET SQLQueries('<code>')?$select=SqlText` — before every execution,
and there is no memoization anywhere in the class (verified: `TryGetSqlQueryTextAsync` has exactly
one call site and no cache guards it).

Since `BuildContentAddressedQueryCode` derives the code from a SHA-256 of the normalised SQL
(`SAPServiceLayerClient.cs:6268`), a code that has been verified once in this process **cannot**
subsequently disagree with the text — and per the known `SQLQueries` lifecycle, these objects are
never deleted. The probe is therefore pure overhead after the first hit.

Concretely, `ExecuteRawSqlQueryAsync` (`:6154`) costs `1 probe + N page reads`. For the typical
single-page report or stock query that is **2 round-trips where 1 would do — a 50% reduction**, on
every report, POD lookup, stock query, price query, batch query, and exchange-rate read.

**Fixed.** `EnsureSqlQueryAsync` now consults an `IMemoryCache` entry keyed on the query code, whose
value is the fingerprint of the statement this process last confirmed SAP holds under it, and
returns immediately on a match. The fingerprint is compared rather than folded into the key
because the fixed codes — `SHOP_ITEM_PRICES`, the per-warehouse stock queries — are legitimately
redefined when their SQL is edited in a release, and that has to still trigger the PATCH.

Two backstops keep a stale belief from surviving: entries expire after an hour (the window in which
a node on a different build could redefine a fixed code), and every execute path
(`ExecuteRawSqlQueryAsync`, `RunStoredSqlQueryAsync`, the three stock executors) drops the entry
when SAP returns a failure, so the next caller re-probes.

Net effect: the single-page report / stock / price query goes from 2 SAP round-trips to 1.

---

## 3. Only one code path in the system marks itself interactive — FIXED

`SapRequestPriority.BeginInteractive()` has exactly **one** call site —
`SalesOrderService.cs:1085` (sales-order approval). Everything else a person waits on — mobile
business-partner list, product list, stock lookup, invoice view, customer statement, quotation view
— queues first-come-first-served against the 6-slot global limiter, alongside price-catalog sync,
POD validation, and the 5–10 second posting jobs.

This is the most likely explanation for user-visible "mobile is slow / times out" symptoms that are
not reproducible on an idle system.

**Fixed.** `SapRequestPriorityMiddleware` opens an interactive scope for every HTTP request, so
background work (Quartz jobs, queue consumers) is classified correctly by omission. It is
registered after `UseOutputCache` so a cache hit claims nothing.

A blanket "all HTTP is interactive" would have been wrong on its own, because some endpoints arrive
over HTTP but behave like jobs — and promoting those would queue an approval behind a report,
exactly the failure the reservation exists to prevent. So the default is interactive, with an
explicit opt-out, `[SapBackgroundWork]`, applied to:

- `ReportController` (whole controller — every report scans months of SAP documents)
- `POST /api/invoice/pods/validate-bulk`
- `POST /api/merchandiser/backfill-product-details`
- `POST /api/desktopintegration/stock/fetch-daily`
- `GET /health/dependencies` — the one health endpoint whose checks reach SAP, and it is polled by
  monitoring rather than waited on by a person

Getting the annotation wrong by omitting a small endpoint costs nothing; omitting a heavy one costs
approval latency, which is why the heavy ones are named explicitly rather than inferred.

**One trap worth knowing about:** the priority is an `AsyncLocal`, so it flows into work *started*
inside a request and never awaited by it. `FetchDailyStock` returns 202 and continues in a
`Task.Run`; without care, a whole-warehouse stock fetch would have inherited the reservation from
the request that kicked it off. `SapRequestPriority.SuppressInteractive()` drops it at that
hand-off, so the endpoint's annotation is not the only thing standing between bulk work and the
reserved slots. `WebhookService` has the same fire-and-forget shape but makes no SAP calls.

The pre-existing scope in `SalesOrderService.ApproveAsync` is now a nested no-op on the only route
that reaches it. It is kept so the guarantee belongs to the approval rather than to the caller
happening to be an HTTP request.

`SapRequestPriorityMiddlewareTests` covers the classification and scope lifetime;
`SapRequestPriorityPipelineTests` runs a real Kestrel server to confirm the thing the registration
actually depends on — that routing has resolved the endpoint by the time the middleware runs, so
the opt-out attribute is visible. If that were not true, every endpoint would silently be
interactive and the reservation would be worth nothing.

---

## 4. N+1 fan-out at specific call sites

### 4.1 Mobile sales-order list runs up to 100 unindexed ORDR scans per page load — FIXED

`Features/SalesOrders/Queries/GetAllSalesOrders/GetAllSalesOrdersHandler.cs:31` calls
`RepairMobileOrdersMissingSapMetadataAsync` on **every** request that specifies a `Source`, before
returning data that is otherwise entirely local. That method (`:362`) loops over up to 100 candidates
and issues one `GetSalesOrderByOrderNumberAsync` each.

Each of those is, by the code's own comment (`SAPServiceLayerClient.cs:11711`), an unindexed scan of
`ORDR` on the `U_OrderNumber` UDF — described elsewhere in this codebase as "the single most
expensive call in an approval" (`:11052`).

So: **up to 100 of the most expensive SAP query in the system, serialised, on a list page load**,
each additionally paying §1 and competing for the 6 shared slots.

Worse, this work is redundant: `SalesOrderReconciliationJob` already does exactly the same repair on
a 2-minute cadence.

**Fixed.** The repair loop is deleted; the source-filtered list path now touches only local tables,
and relinking is owned solely by `SalesOrderReconciliationJob`.

Two things found while doing it, both worth recording:

- **The loop was largely dead code.** Its predicate required `Status == Approved` **and**
  `SAPDocNum` null-or-≤0, but `ApplicationDbContext.EnsureApprovedSalesOrdersHaveSapDocNum`
  (`ApplicationDbContext.cs:36`) rejects any insert or update in exactly that state, and has since
  commit `7d027b2`. So it could only ever match rows predating that guard or written outside EF —
  while paying up to 100 `ORDR` scans on every request to look for them. Worse, any row it *did*
  find and could not resolve stayed a candidate, so the steady state was the same hundred scans on
  every page load, forever.
- **The job's candidate filter had a real gap**, now closed
  (`SalesOrderService.cs:1877`): it matched `SAPDocNum == null` while every other test for "has a
  SAP document" is `HasSapDocNum`, i.e. `SAPDocNum.GetValueOrDefault() > 0`. Rows with a
  non-positive DocNum were being left to the deleted loop. The predicate is now
  `SAPDocNum == null || SAPDocNum <= 0`. This does not increase SAP call volume — the job is capped
  at 25 probes per run regardless — it only changes which 25.

Behaviour is otherwise preserved: the job's `ApplySapDocumentToLocalOrder` sets the same fields the
loop set (`Status`, `SAPDocEntry`, `SAPDocNum`, `IsSynced`, `SyncError`, `UpdatedAt`) plus a
snapshot refresh, so per order it is a strict superset.

The one deliberate narrowing: the loop had no age limit, the job looks back 7 days. An order that
has been unlinked for longer will no longer be picked up automatically. That is the right bound —
it needs manual attention by then, and widening the lookback would worsen §4.2.

`MobileSalesOrderListTests` pins this shut with an `ISAPServiceLayerClient` that throws on any call.

### 4.2 Background reconciliation re-probes the same unreconcilable orders forever — FIXED

`SalesOrderService.ReconcileUnlinkedSapSalesOrdersAsync:1877` selects up to 25 candidates
(`SalesOrderReconciliationJob.cs:17`) with a 7-day lookback, and probes each individually. There is
no attempt counter, backoff, or negative cache. An order that legitimately never reached SAP stays a
candidate and is re-probed every 2 minutes for 7 days — ~5,000 unindexed `ORDR` scans per stuck
order.

**Fixed** by batching rather than by backing off. `GetSalesOrdersByOrderNumbersAsync` resolves the
whole candidate set in one filter of ORed equalities, so a sweep costs one scan of `ORDR` instead of
25 — the run rate drops from roughly 18,000 scans a day to 720. Only the candidates SAP actually
holds then take a posting lock and a re-read; the rest cost nothing beyond that single probe, and
the resolved document is passed into the linking step so the per-order probe does not creep back in
under the lock.

Attempt tracking and exponential backoff would still be worth having, but they need a column on
`SalesOrders` and therefore a migration applied to both databases. Batching removes the cost that
made the absence of backoff matter, without that.

### 4.3 Stock validation fetches the entire warehouse, once per line — FIXED

`StockReservationService.cs:952` and `:1070`, and `BatchInventoryValidationService.cs:416` and
`:1347`, call `GetStockQuantitiesInWarehouseAsync(warehouseCode)` **inside a loop** and then
`FirstOrDefault` a single item out of the result.

`GetStockQuantitiesInWarehouseAsync` (`SAPServiceLayerClient.cs:5934`) runs an
`OITM ⋈ OITW` scan for the whole warehouse and pages it 500 rows at a time
(`ExecuteStockQueryAsync:6352`). For a 5,000-item warehouse that is ~11 round-trips (probe + 10
pages) **per line**. A 20-line order costs ~220 SAP requests to answer 20 single-item questions.

`GetStockQuantitiesForItemsInWarehouseAsync` (`:5973`) already exists, takes an item-code list,
chunks at 100, and is used correctly by `ValidateStockAvailabilityAsync` (`:7476`).

**Fixed.** All four sites now use `GetStockQuantitiesForItemsInWarehouseAsync`:

- `StockReservationService.ValidateStockAvailabilityAsync` reads stock once per warehouse for the
  item codes the request actually mentions (`LoadRequestedStockAsync`), and both the per-line pass
  and the aggregate pass read from that. A twenty-line request goes from ~220 SAP round-trips to
  one call.
- `StockReservationService.GetReservedStockSummaryAsync` asks for its one item instead of scanning
  the warehouse to pick it out.
- Both `BatchInventoryValidationService` sites ask for the single item they are about. Their
  per-group `try`/`catch` degradation is unchanged, so a SAP failure still becomes a warning rather
  than an error.

Also fixed while in the same code: `GetAvailableBatchesAsync` was called once per *requested batch
number* inside the line loop, and again in the aggregate pass, though it depends only on
(item, warehouse). It is now read at most once per pair per validation.

**A cost this introduces, deliberately.** Item codes are embedded in the SQL text, and the SAP query
object is keyed on a hash of that text — so a per-request item set means a new permanent SAP query
object per distinct set. That is the growth behind [[ouqr-cleanup-pending]]. Two things bound it:
the pattern is already established (`SAPServiceLayerClient.ValidateStockAvailabilityAsync` has
always worked this way on the invoice path), and `GetStockQuantitiesForItemsInWarehouseAsync` now
sorts as well as deduplicates its codes, so `{A,B}` and `{B,A}` share one object instead of two.
`GetItemsByCodesAsync` already did this. The same ordering is now applied to
`GetBatchNumbersForItemsInWarehouseAsync` and `GetPackagingMaterialStockAsync`, the other two
methods that embed codes in content-addressed SQL. (`GetPackagingMaterialStockAsync` orders
ordinally to match its case-sensitive `Distinct` — folding case there would change which codes reach
the `IN` clause, and SAP compares them case-sensitively.)

`GetItemPricesForCustomerAsync` was listed here as a third candidate; it is not one. It routes
exclusively through the Items OData API (`GetItemPricesForPriceListViaItemsApiAsync`) and creates no
SAP query object, so canonical ordering buys nothing. Its interface doc claimed it "combines BP
lookup + price fetch into a single SAP SQL query", which has not been true since that path was
replaced; the doc is corrected.

**Noted in passing, not fixed:** `GetReservedStockSummaryHandler.cs:24` loops up to 100 items
calling `GetReservedStockSummaryAsync`, which makes its own SAP call each time. The per-call cost
just dropped by roughly an order of magnitude, but it is still an N+1 and wants the same treatment
— it needs the single-item interface method reshaped, so it is its own change.

`StockReservationValidationTests` asserts the read *pattern*, not just the verdict — the answers
were never wrong here, only the cost.

### 4.4 Account sales/payment report: two sequential fan-outs over accounts — FIXED

`Features/Reports/Queries/GetAccountSalesPaymentReport/GetAccountSalesPaymentReportHandler.cs:167`
and `:201` loop over `accountCodes` calling `GetInvoicesByCustomerAsync(…, includeDocumentLines:
true)` and `GetIncomingPaymentsByCustomerAsync` per account, sequentially. With document lines the
page size drops to 100 (`SAPServiceLayerClient.cs:1771`), so a busy account is several round-trips on
its own.

**Fixed.** `GetInvoicesByCustomersAsync` and `GetIncomingPaymentsByCustomersAsync` take the whole
account set and filter on ORed `CardCode` equalities, chunked at 25 because the filter travels in the
URL. The report already de-duplicated afterwards, so a single combined result set was a drop-in
replacement.

### 4.5 Sales-order UoM fallback probes SAP per item

`SalesOrderService.cs:2402` loops `missingItemCodes` calling `GetItemByCodeAsync`. See §5.2 for why
each of those calls is also individually expensive.

---

## 5. Over-fetching

### 5.1 Document list queries request full documents including all line collections — FIXED

Of the 107 OData URLs built in `SAPServiceLayerClient`, 79 have no `$select`. `Invoices` and
`BusinessPartners` are handled well (`:1680`, `:7057`) — the rest are not. These list endpoints all
fetch complete marketing documents, which in the Service Layer means every header field **plus
`DocumentLines` and their nested collections** (batch numbers, serial numbers, tax lines,
distribution rules):

- `StockTransfers?$filter=…` (4 variants)
- `PurchaseOrders`, `PurchaseRequests`, `PurchaseQuotations`, `PurchaseDeliveryNotes`,
  `PurchaseInvoices` (list + by-supplier + by-date-range each)
- `CreditNotes` (list, by-customer, by-date-range)
- `Quotations` (list, by-customer, by-date-range)
- `IncomingPayments` (list, by-customer, by-date-range)
- `InventoryTransferRequests`
- `Warehouses`, `ChartOfAccounts`, `ProfitCenters`

**Fixed**, and the blocker is gone: the `$metadata` snapshot is now committed at
`reference/sap-service-layer-metadata.xml`.

All 49 URLs now carry a `$select`, expressed as one constant per bound model. Each list is exactly
what its model binds, generated from the models and checked field-by-field against the metadata.
`SapSelectClauseTests` re-checks every constant against that file on each run — an unknown name is
not a silently ignored hint, it makes SAP answer 400 and breaks the endpoint, so this needs a
control rather than a one-off hand-check. It also catches the reverse mistake: adding a property to
a model without widening the select, which would leave the property silently null.

The metadata settled two things I had reasoned about but could not verify:

- `Orders`, `Invoices`, `Quotations`, `CreditNotes`, `PurchaseOrders`, `PurchaseInvoices`,
  `PurchaseQuotations` and `PurchaseDeliveryNotes` really are all `SAPB1.Document` — one type with
  328 properties — which is what makes a single field list safe across them. Pinned by a test.
- Header-level `BaseEntry`/`BaseType` on `SAPCreditNote`, which I had flagged as probably wrong,
  are **valid** on `Document`. Good thing that was checked rather than acted on.

`StockTransfers` and `InventoryTransferRequests` share `SAPB1.StockTransfer`; `IncomingPayments` is
`SAPB1.Payment`.

Lines are kept in the select (`$select=…,DocumentLines` works here). The saving is the ~300 unbound
header fields and every *other* nested collection — tax lines, batch and serial allocations,
distribution rules, freight, approval requests. Dropping the line collection per endpoint is a
further and larger win, but it is a behaviour decision rather than a mechanical one, so it is not
done here.

The reference-data sets `Warehouses`, `ChartOfAccounts` and `ProfitCenters` are still excluded:
they are small flat entities parsed by hand rather than through a bound model, and the real win for
them is §6 (cache them) rather than `$select`.

### 5.1a Two models bind fields that do not exist in SAP — found by the metadata check, FIXED

Validating the models against `$metadata` turned up properties that no SAP type defines, so they
have always deserialized to null/zero. Omitting them from `$select` changes nothing — they were
never populated — but they are live bugs in their own right.

**`IncomingPayment` (`SAPB1.Payment`)** binds six fields the type does not have: `DocDueDate` (the
real name is `DueDate`), `CheckSum`, `TransferSumFC`, `CreditSum`, `DocTotal` and `DocTotalFc`.
Cheque and credit-card amounts live in the `PaymentChecks` and `PaymentCreditCards` collections,
which the model already binds, and `Payment` has no header total at all.

This is not cosmetic. `GetAccountSalesPaymentReportHandler.GetPaymentTotal(IncomingPayment)` reads:

```csharp
var methodTotal = payment.CashSum + payment.CheckSum + payment.TransferSum + payment.CreditSum;
if (methodTotal != 0m) return methodTotal;
return payment.DocTotal != 0m ? payment.DocTotal : payment.DocTotalFc;
```

`CheckSum` and `CreditSum` are always 0, and so are both fallbacks. **A payment made entirely by
cheque or card is counted as zero, and a mixed payment counts only its cash and transfer parts.**
That under-reports collections and the collection-rate percentages in the account sales and payment
report. The local-entity overload just below it uses real columns and is unaffected, which is
probably why this has gone unnoticed.

**Fixed.** `CheckSum`, `CreditSum` and `DocTotal` are now computed on the model from the rows SAP
does return, and are get-only so nothing can mistake them for wire fields again. `DocTotalFc` and
`TransferSumFC` are gone; `DocDueDate` is now bound as `DueDate`, and `PaymentInvoice.SumAppliedFC`
as `AppliedFC`, which are the names SAP uses.

Keeping the property *names* meant every existing caller became correct without being rewritten —
the compiler found the only two that genuinely had to change. Three further copies of the same
broken fallback in `IncomingPaymentService`, and a fourth in the DTO mapping, are now correct and
have been simplified to read `DocTotal` directly.

The blast radius was wider than the report: `MappingExtensions.ToDto` carried the same
`DocTotal > 0 ? … : CashSum + TransferSum + CheckSum + CreditSum` fallback, so
`IncomingPaymentDto.DocTotal`, `.CheckSum` and `.CreditSum` were wrong for every API consumer too,
not just the report.

`IncomingPaymentSelect` had to grow `PaymentChecks` and `PaymentCreditCards` — they are not optional
detail on a payment, they are the only record of what was paid by cheque or card, so dropping them
to save payload would silently understate every non-cash payment. That is noted at the constant.

`IncomingPaymentTotalTests` covers the cheque-only case that used to be worth zero, the card-only
case, a mixed payment, the cash-and-transfer case that always worked, empty collections, and a
round-trip from real SAP field names.

Not handled: a cheque or card row drawn in a different currency from the payment header. Amounts are
summed in the payment's own currency, which is how the rest of the reporting treats a payment; the
model documents the assumption rather than hiding it.

**`InventoryTransferRequest` (`SAPB1.StockTransfer`)** binds `RequesterEmail`, `RequesterName`,
`RequesterBranch` and `RequesterDepartment`, none of which exist on the type — the approval metadata
lives under `StockTransfer_ApprovalRequests`. Always null today. Left as-is: unlike the payment
fields nothing computes with them, so they are cosmetically wrong rather than arithmetically wrong,
and populating them properly means reading a different collection.

### 5.2 `Items('{code}')` pulls the entire item master record — FIXED

**Fixed** alongside §6, since it is the same method. `GetItemByCodeAsync` now sends `ItemSelect` —
the 15 scalar fields `Models/Item.cs` binds, validated against the metadata and covered by
`SapSelectClauseTests` like every other select. Without it SAP returned the whole item: every price
list under `ItemPrices`, a row per warehouse under `ItemWarehouseInfoCollection`, and the UoM and
packaging collections. `GetAllItemsAsync` already got this right with an 8-field select; the
single-item path did not.

### 5.3 Customer statement aging pulls every invoice a customer has ever had — FIXED

`Features/Statements/Queries/GetCustomerStatement/GetCustomerStatementHandler.cs:200` fans out over
`cardCodes` (in parallel, which is at least right) into `GetInvoicesByCustomerAsync(cardCode)` —
the unbounded overload (`SAPServiceLayerClient.cs:1686`) that pages until exhaustion with no date or
status filter. It then keeps only invoices with `Balance > 0`.

**Fixed.** `GetOpenInvoicesByCustomersAsync` filters `DocumentStatus eq 'bost_Open' and
Cancelled eq 'tNO'` for the whole card-code set at once, so the handler no longer fans out per
customer into the unbounded overload and discards almost everything it reads. `bost_Open` is
confirmed against the metadata as a member of `BoStatus`.

`Cancelled` is checked as well as the status. A cancelled invoice normally closes, but the code this
replaced excluded cancellations explicitly, and that should not quietly become an assumption about
SAP's bookkeeping. No date floor: aging is about what is outstanding now, and an old unpaid invoice
is exactly what it needs to see.

---

## 6. Reference data that is re-fetched instead of cached — FIXED

The class already cached warehouses (5 min), business partners (30 min), G/L accounts (30 min),
price lists (60 min), warehouse item codes (2 min), item UoM (6 h) and document series (6 h). These
were not cached, despite being equally static:

| Method | What it does now |
|---|---|
| `GetCostCentresAsync` | cached 60 min — its own interface doc already said it "should be cached locally" |
| `GetCostCentresByDimensionAsync` | filters the cached list; it was a second paged walk of `ProfitCenters` for one extra predicate |
| `GetCostCentreByCodeAsync` | answered from the cached list, falling back to SAP on a miss |
| `GetCurrenciesAsync` | cached 60 min |
| `GetPaymentTermsByCodeAsync` | cached 60 min per group — a customer statement reads this once per account |
| `GetItemByCodeAsync` | cached 30 min per code, and now `$select`ed (§5.2) |

A shared `GetOrLoadReferenceDataAsync` carries the double-checked locking for the whole-list caches,
rather than repeating the ~35-line block the three existing caches each spell out. Those three also
report into the sync-status UI through `CacheStatusKeys`; the lists added here are not on that
surface, so the helper does not.

Two judgement calls worth recording:

- **`GetCostCentreByCodeAsync` is not answered from the cache alone.** The cached list is filtered to
  `Active eq 'tYES'`, but the by-code lookup is by key and has no such filter. Serving misses from
  the cache would have quietly turned every inactive cost centre into "not found". It checks the
  cache, then falls back to SAP — free in the normal case, unchanged in the edge case. There is a
  test for each.
- **Per-key caches (payment terms, items) have no gate.** A single shared lock would make two
  unrelated item lookups wait on each other; the worst a race costs is a duplicate read of the same
  immutable record.

## 7. Correctness / bounding issues found along the way — FIXED

A rescan for interpolations into query strings found rather more than the two originally listed, so
this section grew. Everything below is now fixed.

### Injection

- **Raw SQL, unsanitized — the most serious of these.** `GetSalesQuantitiesByWarehouseAsync` put
  `warehouseCode` straight into a HANA statement (`WHERE T1."WhsCode" = '{warehouseCode}'`) with no
  `SanitizeSqlValue`, unlike every other SQL builder in the file. Reachable from two HTTP handlers
  (`GetSalesInWarehouse`, `GetSalesInWarehousePost`) with the warehouse code as a caller-supplied
  parameter. Now sanitized.

- **OData injection in `SearchBusinessPartnersAsync`.** Interpolated `searchTerm` straight into
  `contains(CardCode,'…')`, reachable from `SearchBusinessPartnersHandler`. Fixed, but *not* with
  `SanitizeODataValue`: that rejects a quote outright, which is right for an identifier and wrong
  for a search box — refusing to look up "O'Brien" is a bug, not a defence. A new
  `EscapeODataStringLiteral` doubles the quote instead, and the finished expression is now
  URL-encoded so an `&` in the term cannot close `$filter` and append parameters of its own.

- **Eleven `CardCode` filters and three key segments** were interpolated unsanitized:
  `PurchaseOrders`/`PurchaseQuotations`/`PurchaseDeliveryNotes`/`PurchaseInvoices`/`CreditNotes`/`Orders`
  by-customer URLs, the five matching `$count` filter builders, and
  `BusinessPartners('…')` / `ChartOfAccounts('…')` / `ProfitCenters('…')`. All now use
  `SanitizeODataValue`. Also `InventoryTransferRequests`, where the filter was passed through
  `Uri.EscapeDataString` — which does not help, since the server percent-decodes before parsing the
  filter.

Checked and found already safe: the sales-order search filter (sanitized on the line above the
interpolation), the `ItemCode eq` batches (built from pre-sanitized collections),
`documentStatus`/`cancelled` (mapped from an enum to literals), and the POD exclusion card codes
(configuration, not input).

### Bounding

`GetIncomingPaymentsAsync`, and the by-customer/by-supplier lists for `PurchaseOrders`,
`PurchaseQuotations`, `PurchaseDeliveryNotes`, `PurchaseInvoices`, `CreditNotes` and `Quotations`,
all issued a single request with no `$top`, no `Prefer: odata.maxpagesize` and no paging loop. They
returned whatever the Service Layer's default page happened to be — about 20 rows — while their
names and return types promised the lot. **This is a silent wrong answer, not a slow one:** a
supplier with 60 purchase orders showed 20, with nothing to indicate the rest existed.

All now go through one `ReadDocumentPagesAsync` helper that walks pages of 500 with an explicit
`$top`, and stops at a 2,000-row ceiling. The ceiling exists because these lists carry no date
bound, so a long-standing trading partner's history is otherwise unbounded; reaching it logs a
warning rather than passing silently, since that is the point at which the caller needs a date
filter.

- **`GetBusinessPartnersHandler` has no pagination.** `:26` returns every customer and supplier in one
  response. On a cold 30-minute cache the first caller also pays ~N/500 sequential SAP page fetches.
  This is a strong candidate for mobile business-partner list failures. `SearchBusinessPartnersAsync`
  (top 50, server-side filter) already exists and should back a paged/filtered list endpoint.

- **`HttpResponseMessage` is frequently not disposed** on the hand-rolled request paths
  (`GetSalesOrderByOrderNumberAsync:11724`, `ExecuteStockQueryAsync:6372`,
  `ExecuteRawSqlQueryAsync:6177`, `CreateSalesOrderAsync:11160`, and others). Content is fully read so
  connections do return to the pool, but this adds finalizer pressure. The
  `SendSapRequestWithTransientRetryAsync` + `using var responseOwner` pattern used elsewhere in the
  same file is the right shape.

---

## 7a. What the first real SAP run found

The `$select` work in §5.1 was validated against the committed `$metadata` and by a test that
re-checks it. That was necessary and insufficient, and the first run of
`ShopInventory.IntegrationTests` against a live Service Layer found four defects.

**`$metadata` does not tell you what a given entity set accepts.** `Document` is one EDM type shared
by every marketing document, so its property list is the union across all of them, while SAP
validates `$select` against the entity set at runtime. Three fields passed the metadata check and
were refused:

| Entity set | Field | Why |
|---|---|---|
| `PurchaseRequests` | `DocTotal` | a purchase request carries no document total |
| `CreditNotes` | `BaseEntry`, `BaseType` | line-level on an A/R credit memo, not header |

All three were already inert — `DocTotal` was read as `?? 0`, and `ResolveOriginalInvoiceDocEntry`
already fell back to the line-level `BaseEntry` — so dropping them from the selects restores
behaviour rather than changing it.

**A pre-existing model bug with no coverage.** `SAPPurchaseRequest.Requester` was `int?`; SAP returns
a user code such as `"Wkshop2"`. Deserialization threw on every purchase request that had a
requester, which is every real one. It predates this audit entirely; nothing had ever exercised the
path. Now `string?`, on the model and on the read DTO.

**A UDF is not a schema fact.** `U_OrderNumber` was rejected on `Orders` and `Quotations` on the
first run, which looked like a fourth bad field. It is not: `UserFieldsMD` shows the UDF defined on
32 tables including ORDR and OQUT in `KEFALOS_USD_NEW2`, and on neither in `KEFALOS_TEST_3`.
Removing it would have broken sales order duplicate prevention in production. UDFs are per company
and per table, and no static check can settle them.

Two consequences worth carrying:

- **The test company could not run the integration tests, and now can.** `KEFALOS_TEST_3` was
  missing `U_OrderNumber` and `U_Van_saleorder` on all 32 marketing-document tables, so the suite
  only passed against production. They have been added, mirroring the production definitions
  (`db_Alpha`, size 100, not mandatory). Two `POST /UserFieldsMD` calls were enough for all 64:
  B1 propagates a UDF added to one marketing-document table across the whole family, so `ORDR` and
  `OINV` carried the rest. The middle call returned `400 No records`, which is what SAP says when
  the cascade has already created the field. The suite now passes against `KEFALOS_TEST_3` with no
  override.
- **Beware the default page.** This caught three separate ad-hoc queries during the audit. The
  `UserFieldsMD` query used to settle whether the UDF existed in production returned exactly 20
  rows with ORDR absent, and very nearly produced the wrong conclusion. Verifying the UDF creation
  afterwards did it again — `$top=500` alone is not enough, because `$top` bounds the result while
  `Prefer: odata.maxpagesize` governs the page, and without the header SAP still answers 20 with a
  nextLink. It is the same defect §7 fixes in the client, and it keeps recurring in throwaway
  diagnostics precisely because those do not go through the code that now handles it.

## 8. What is already good

Worth stating so it is not undone:

- Static session with double-checked locking and CAS-guarded 401 re-auth (`:245`–`:300`).
- Split interactive/background concurrency limiter with a reserved interactive floor
  (`SAPConcurrencyHandler`).
- Circuit breaker + transient classification + bounded retry.
- Content-addressed `SQLQueries` codes, which is what stopped the per-request query leak.
- `ReportService` caching with hit-rate telemetry.
- The sales-order UoM resolver's three-tier cache (memory → durable store → SAP) and its
  out-of-band warm job.
- `GetInvoiceHeadersByDocEntriesAsync` / `SqlIdRangeCover` range-bucketing.

---

## Suggested order of work

1. ~~§1 — async SAP request logging.~~ **Done.**
2. ~~§2 — memoize `EnsureSqlQueryAsync`.~~ **Done.**
3. ~~§4.1 — delete the in-request mobile order repair loop.~~ **Done.**
4. ~~§3 — interactive scope in middleware.~~ **Done.**
5. ~~§4.3 — swap the looped whole-warehouse stock fetches for the existing scoped variant.~~ **Done.**
6. ~~§5.1 — add `$select` to the document list URLs.~~ **Done.**
   Unblocked by the committed `$metadata` snapshot.
7. ~~§7 — the injection fixes and the unbounded/truncated queries.~~ **Done.**
8. ~~§5.1a — the incoming-payment total.~~ **Done.**
9. ~~§6 and §5.2 — cache the reference data, `$select` the item master.~~ **Done.**
10. ~~§4.2, §4.4 and §5.3 — the remaining fan-outs.~~ **Done.** Every item in this audit is now addressed.
