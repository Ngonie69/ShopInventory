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

- **The van handset's invoice history (`POST /api/vansales/order/history`) now reports a per-sale
  invoice as fiscalised, with the sale's own receipt, and carries the sale number beside the SAP
  identity.**

  A van sale is signed on the handset under its own reference and reaches SAP afterwards, one
  invoice per sale, so nothing in the fiscal transaction log names its DocNum. The history read only
  that log, and every such invoice came back `fiscalized: 0` with no verification code, QR, fiscal
  day or device — "Fiscalised: No" on the very phone that printed the receipt — while the Van Sales
  drawer showed it signed. The read now also asks the sale behind the invoice, through the same
  registry the invoice list and PDF use, and answers with the sale's receipt.

  Two more fields moved with it. `due_date` (the handset's "Sale date") and `timestamps.create_date`
  are the moment the receipt was signed rather than the SAP document date; and an invoice with no
  sale behind it now reports that document date as `00:00:00`, where it used to read the midnight-UTC
  value SAP serialises as an instant and show `02:00:00`. A new `sale_number` field carries the number
  the office quotes — `INV2327` — and is empty for an invoice that records no sale.

  `id` is unchanged and is still the SAP DocEntry, because the handset sends it back as the document
  to file a proof of delivery against, and the server reads that number as a sales order id first. A
  handset that wants to title the invoice `INV2327` rather than `INV{DocEntry}` reads `sale_number`.

- **An online van sale SAP refused can be posted from the Van Sales → Invoices drawer, the way
  Desktop Sales posts a held till sale.**

  A van sale is signed before SAP is asked, and when SAP then refuses the invoice the queue retries
  once and parks the sale for review — after which nothing offered it again, and
  `POST /api/DesktopIntegration/sales/{reference}/post` refused its receipt row as "already in SAP"
  because that row is written Consolidated before SAP is ever asked. The post now takes an online van
  sale's receipt row (signed, no SAP number) through its reservation, with fiscalisation off, and
  closes the invoice queue entry that was waiting for it; `outcome` is `Posted` or `AlreadyInSap` as
  for a till sale. A receipt row that already carries a SAP number is refused as before, and an
  unsigned one is refused with where the sale is signed instead (the handset's resend, or the queue
  entry's Retry in the Exception Center).

  `GET /api/van-sales/invoices/{reference}` gains `postRefusal` and `fiscaliseRefusal`: the reasons
  those two commands would refuse the invoice, null where they would not — the same rules Desktop
  Sales offers its buttons on. The drawer at `/van-sales/invoices` offers **Post to SAP** and
  **Retry fiscalisation** on exactly those terms, and states the reason in the buttons' place
  otherwise. A converted order, which has no sale row of its own, is pointed at the Exception Center.

  `ConfirmReservationResponseDto` gains `alreadyPosted`, true when the confirm found the invoice in
  SAP rather than creating it. Additive.

- **`GET /api/DesktopIntegration/sales/analysis` breaks each currency's takings down by business partner.**

  Every currency section now carries `byBusinessPartner` beside `byWarehouse`: one row per CardCode the
  sales were made as, labelled by the name those sales carried (the code where none did), with the same
  sales count, takings, share and split by payment method as the other breakdowns. Nothing existing
  moved. It backs the new "Business partner" view of "Who took it" on `/reports/desktop-sales`, the
  "By Business Partner" sheet of that page's Excel export — the partner's name with its card code
  beside it — and `/desktop-sales`, whose "Largest on the device" card now names the customers rather
  than listing their codes.

- **SAP user accounts can be unlocked and given a new password from the back office.**

  `GET /api/sap-users`, `POST /api/sap-users/{internalKey}/unlock` and
  `POST /api/sap-users/{internalKey}/password`, behind the new `sapusers.view`, `sapusers.unlock` and
  `sapusers.change_password` permissions — Admin holds all three by default and no other role holds
  any. The screen is `/sap-users`, under Administration.

  Two things an operator should know. **These are SAP Business One's own logins, not this
  application's accounts** — resetting one here does nothing to the person's ShopInventory sign-in,
  and the page says so. And **the Service Layer account this application signs in as must be a SAP
  superuser** for either write; where it is not, SAP refuses and says which authorisation is missing,
  and that sentence is what the page shows. Creating and removing SAP users is still done in the B1
  client, because it is a licensing decision.

- **Van sales document lists take new filters and return a period summary.**

  `GET /api/van-sales/invoices` takes `channel` (`Online` or `Offline`) and returns `summary`: online
  and offline counts, totals per currency with `netOnlyCount` (sales whose amount carries no VAT), and
  what SAP has not invoiced yet, per currency and by van. `GET /api/van-sales/invoices/{reference}`
  returns `creditNotes`, each with `givesBack` saying whether its amount actually came off the invoice.
  `GET /api/van-sales/credit-notes` takes `origin` (`SAP` or `Till`) and `includeCancelled` (default
  `true`) and returns `summary`; each credited invoice gains `amount`, `amountIncludesVat`, `currency`,
  `soldOn`, `channel` and `warehouseCode`. All additions; an unknown `channel` or `origin` is a `400`.

- **`transfer-listener/status` reports delivery and the ledger; the `transfer-listener` health check
  goes Unhealthy when transfer lines stop reaching the ledger.**

  On 2026-09-17 TransferEventListener read SAP on time while posting every line to
  `http://10.10.10.9/api/...` — port 80, where IIS answers 404 — so no transfer since the previous
  morning had reached local stock, and both the page and `/health/dependencies` said healthy. The
  status reply now adds `delivery` (lines waiting, the oldest wait, lines given up and rejected, the
  URL the listener posts to, and its last answer) and `ledger` (stock movements applied today from
  `StockTransferAdjustments`, the last one applied and per-warehouse document counts — read from this
  API's database, so present even when the listener is down). Each recent document gains `localStock`
  (`Applied`, `Waiting` or `NotApplied`) and `appliedAtUtc`. `poll` gains `resumedFromSavedState` and
  `processedDocuments`. `check-now` adds the listener's `notificationsDelivered`, `notificationsQueued`,
  `notificationsReplayed`, `notificationsRejected`, `notificationsAbandoned` and `pendingNotifications`.
  All additive; the delivery figures read zero or null against a listener older than
  TransferEventListener#11.

  The health check is **Degraded** once a line has waited `PollStalenessWarningMinutes` (20) and
  **Unhealthy** at `PollStalenessCriticalMinutes` (60), naming the last answer and URL.

- **`GET /api/DesktopIntegration/sales` now takes the whole filter surface, and can count it.**

  It filtered on one warehouse, one consolidation status and one channel, sorted on nothing, and
  counted nothing. It now also takes the repeatable `warehouses`, `consolidationStatuses`,
  `fiscalizationStatuses`, `paymentMethods` and `sourceSystems` — the many-value forms of those
  filters, combined with the singular ones rather than replacing them, so every existing caller is
  unchanged — plus `minTotal`/`maxTotal` (inclusive), `paymentDifference` (`any`, `exact`, `under`,
  `over`, comparing what was tendered with what was rung up) and `sort` (`newest`, `oldest`,
  `total-desc`, `total-asc`, `customer`). `search` now matches the customer's code and name as well.

  `includeFacets=true` — off by default, because it is five grouped counts a polling till has no use
  for — adds `facets`: for each of those five groups, every value present and how many sales would
  match if it alone were selected. Each group is counted with **its own** selection lifted and every
  other filter still applied, so a chip built on it says what pressing it would give. It also adds
  `unfilteredCount`, what the period holds before any of the request's own filters narrowed it.

  Naming a channel still turns off the default scope that hides the online van receipt carriers, and
  a shop-scoped caller naming any warehouse but their own is still refused rather than narrowed —
  including when the set they send contains one they may read. The `/desktop-sales` console is
  rebuilt on all of it: a search, chip groups carrying their counts, an amount window, a tender
  comparison, a row of pills naming what is set, a sort and a rows-per-page control.

- **A shop-till sale that fails to fiscalise is now recovered, automatically and on request.**

  A `KefalosShopTill` sale fiscalises inline, and one failed attempt used to be final: the fiscalisation
  sweep read vending (and, under REVMax, offline van) sales only, and the posting job takes fiscalised
  sales only, so the sale was never retried and never invoiced. The sweep now also takes a till sale
  that is `Failed`, or still `Pending` ten minutes after it was rung up (a request that died). Every
  such retry asks the device for an existing receipt under the sale's invoice number before sending
  anything, so a receipt an earlier attempt did file is adopted, QR and verification code included.

  New `POST /api/DesktopIntegration/sales/{externalReference}/fiscalise` retries one sale now, ignoring
  the attempt budget and lookback. Refused (400) for a sale already fiscalised or skipped, a source this
  system does not fiscalise, a till sale whose own request may still be in flight, and — under the
  platform only — a sale marked for reconciliation. `GET /api/DesktopIntegration/sales` rows gain
  `fiscalError`, `fiscalizationAttempts`, `fiscaliseRefusal` and `canRetryFiscalisation`. The desktop
  sales drawer shows the device's error and a **Retry fiscalisation** button.

- **Desktop fiscal credits: fiscal-only credits on posted sales, and credits filed elsewhere count.**

  `POST /api/DesktopIntegration/sales/{reference}/credit-notes` with `postToSap: false` was refused once
  the sale was in SAP. It is now accepted when the request also sends `saleInSap: true` (what the form
  showed), and saves `sapStatus` `FiscalOnly`: ZIMRA gets the credit, SAP gets no memo and no units go
  back on the ledger. Without `saleInSap: true` it is still refused, so a form read before the sale
  posted cannot silently skip the memo.

  Both `prepare` and the create call now subtract credits ZIMRA already holds against the receipt
  under the customer's SAP credit memo numbers (`source.externalCreditedAmount`,
  `source.externalCredits`), and `prepare` answers `remainingAmount`. A credit worth more is refused.
  **For a sale in SAP, a SAP or REVMax lookup that cannot answer now refuses the credit** where it
  previously went ahead; a sale with no SAP invoice is not checked.

- **Desktop fiscal credits now raise their SAP credit memo by themselves.**

  `POST /api/DesktopIntegration/sales/{reference}/credit-notes` filed the ZIMRA credit and left the
  back office to reconcile by hand. It no longer does: once ZIMRA accepts the credit, the SAP credit
  memo follows automatically — at once if the sale is already in SAP, otherwise the moment that sale
  posts, which for a till sale is that evening. A sweep behind it retries anything SAP refused.

  Every saved credit therefore carries two new fields. `sapStatus` is `Deferred` (the sale has not
  posted yet — the ordinary case at a counter), `Posted`, `Failed`, `NotRequired` (the sale was
  excluded from posting) or `ManualInSap`; `sapError` says why when there is a reason. The existing
  `status` is unchanged and still means the fiscal half alone, and existing clients can ignore both
  new fields. **Nothing is raised in SAP against a credit whose `status` is not `Fiscalised`.**

  Two cases stay a person's job and now say so explicitly rather than being silently pending: a sale
  that reached SAP inside a consolidated invoice has no invoice of its own to credit, and a credited
  receipt line that cannot be tied back to an invoice line is refused rather than guessed at. Both
  report `ManualInSap` with the reason.

  The credited units are also returned to the shared stock ledger now, as soon as ZIMRA accepts the
  credit rather than when SAP sees it — they never left the ledger through SAP, so a shop that took a
  return in the morning is no longer refused sales for it all afternoon. Cash refunds remain a counter
  action and are unaffected.

  Migration `20260912020300_AddDesktopCreditNoteSapPosting` backfills existing credits as `Deferred`,
  so any raised before this deploy get their memo rather than being stranded.

- **`GET /api/DesktopIntegration/sales/analysis`** (Admin, Manager, Cashier, ApiUser).

  A period's till takings broken down by how they were paid — and by day, hour, shop, source,
  operator and best-selling item — with one section per currency, since tills sell in both USD and
  ZWG. It backs the new `/reports/desktop-sales` page and its Excel export. The shop till now asks the
  cashier for Cash, Swipe or EcoCash and sends it as `paymentMethod`, with the EcoCash confirmation as
  `paymentReference`; until now every till sale was sent as cash whatever the customer paid with, so
  earlier sales in this report are cash by that assumption rather than by record.

  Scoped exactly as `GET /sales` is: an account assigned to a shop is narrowed to it, and naming
  another shop's warehouse is refused. Logged as `ViewDesktopSales`. Tenders are grouped by their
  canonical names, so rows stored in a till's own casing or under the legacy `transfer`/`paynow`
  values fold into one line each rather than splitting; a sale with no tender recorded is its own
  `Not recorded` line and is never counted as cash.

- **`POST /api/DesktopIntegration/sales/{externalReference}/post` and
  `POST /api/DesktopIntegration/sales/post-batch`** (Admin, Manager, Cashier, ApiUser).

  A held till, vending or van sale can now be sent to SAP on request, singly or as a chosen set,
  instead of only by the background pass. The pass gives up after a few attempts and a sale it has
  parked is invisible to every later pass, so until now a sale SAP had refused — usually for
  something an operator could fix in a minute — could not be sent at all without editing the
  database. The attempt cap is therefore deliberately not applied here; nothing else is relaxed.

  The batch answers `200` with a row per sale rather than a single status, because each sale becomes
  its own SAP document and there is nothing to roll back. Rows carry an `outcome` of `Posted`,
  `AlreadyInSap`, `InProgress`, `Failed` or `NotPostable`.

  Both are safe to send twice, and re-sending a batch that outlived a timeout is the intended
  remedy. See the behaviour change below for why.

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

### Changed

- **The Stock Write-offs item picker lists every active item, from the Web's own catalogue.**

  `/stock-write-offs` used to fill its item picker with the items holding stock in the chosen
  warehouse, read from SAP through `GET /api/product/warehouse/{code}/paged` a hundred rows at a
  time, every time a warehouse was chosen. It now reads the Web's `CachedProducts` table once per
  visit and offers every active item, whatever the warehouse holds. What an operator sees: the
  picker is ready at once, an item with no stock in the warehouse can be picked, and it is then the
  batch picker ("No batch here holds stock") or the post itself ("does not hold enough stock in
  SAP") that says so. The per-item batch read and the stock check at post time are unchanged.

- **A till sale whose SAP post got no clear answer is no longer looked up every minute.**

  When a post leaves for SAP and the reply is lost, the sale is held for
  `DesktopSalePosting:UnresolvedPostGraceMinutes` (default 15) so it is not invoiced twice. The
  minute-by-minute pass used to load every held sale and ask SAP whether it held the invoice — a
  scan of every invoice for one UDF, fifteen times per hold, for an answer the hold said could not
  be trusted yet. The pass now leaves held sales out until the hold ends and asks once. What an
  operator sees: a post that did land but whose reply was lost is adopted when the hold ends rather
  than a minute or two later, and `/desktop-sales` shows it as held until then. Pressing Post to
  SAP still asks straight away. Held sales also no longer fill the batch: after an outage
  twenty-five of them could be the whole batch, and nothing behind them posted until they cleared.

- **The Web's warehouse stock cache reads a warehouse in one request and keeps it for fifteen minutes.**

  `WarehouseStockCacheService` walked `GET /api/stock/warehouse/{code}/paged` a hundred rows at a
  time, and each page cost SAP a fresh execution of the warehouse's stock query; a warehouse of four
  thousand items was forty executions per sweep, re-armed every five minutes for every warehouse
  somebody had open. It now reads `GET /api/stock/warehouse/{code}` once and serves the rows for
  fifteen minutes. Stock figures on the dashboard, the product page and the transfer pickers can
  therefore be up to fifteen minutes old; the posts that must be exact validate against SAP
  themselves. A read that fails is now recorded as a failure rather than as an empty warehouse.

- **A van sales SAP credit memo names the shop, not the van.**

  `GET /api/van-sales/credit-notes` returned a memo's own `CardCode`/`CardName` as its customer, which is
  the van's posting account shared by every shop on the round. Those rows now carry the route customer
  of the invoice the memo reverses, falling back to the memo's card when the invoice has none.

- **A card swipe now settles as bank transfer money, against a configured G/L account.**

  `SAP:SwipeCreditCardCode` was the only route a swipe could take, and it could never be set: the
  company database has no credit card records at all. So every card sale since the daily payment went
  live was invoiced and then left open — 15 of them on 2026-09-15 alone, 596.83 that the cash desk had
  already counted and banked, which is why the system's settlement kept coming up short of the count.

  Set **`SAP:SwipeTransferAccount`** to the G/L account card takings are banked into and swipes settle
  as transfer money on the day's payment, which is what the money actually does: the acquirer pays the
  bank. It takes precedence over `SAP:SwipeCreditCardCode`, which still works for anyone carrying card
  money on a card account. With neither set, a swipe is still invoiced and left `Unmapped`, and the
  reason now names both settings.

  Waiting sales settle themselves: the daily payment looks back seven days, so swipes left `Unmapped`
  within that window join the next pass once the account is configured.

  **Setting it:** the deploy owns the value. Set the repo variable `SAP_SWIPE_TRANSFER_ACCOUNT` (or pass
  `-SapSwipeTransferAccount <G/L account>` to `Update-Production.ps1`) once, and every later deploy
  carries it forward from the live slot the way the SMTP password is carried — a blue/green slot's
  web.config arrives from the package without it, so a hand-edited value on the server would be lost at
  the next cutover. A deploy that finds it configured nowhere says so in its log.

  **One limit worth knowing:** SAP carries one transfer account per payment. On a day whose takings
  include both card and mobile wallet money, the account is left off and SAP's default applies to the
  whole transfer sum — posting wallet takings into the card settlement account would be an error only a
  hand reconciliation could find. The daily payment logs a warning naming the payment when that happens.
- **A node running an older build than the rest of the cluster no longer runs background jobs.**

  Quartz hands each trigger to whichever clustered node takes it first, so a node nobody deploys to
  keeps doing its share of the work with the code it last received. On 2026-09-15 that settled 45
  till sales with an incoming payment each, weeks after the per-sale payment was replaced by the one
  daily payment per customer, because `DEV-TEST-SERVER` was still a cluster member on a 2026-09-12
  build.

  Each API process now records the build it runs in `ClusterNodes` and refreshes it every minute. If
  a node that is heart-beating holds a newer build, this node vetoes every job fire and logs why,
  while continuing to serve HTTP normally. Builds that no deploy published — every developer build —
  neither block nor are blocked. A rollback needs nothing switched off: once the newer node stops, its
  registration goes stale within five minutes (immediately on a clean shutdown) and the older build
  resumes.

  **For operators:** a node that is quietly doing no background work is now a supported state, and the
  reason is in its log and in `ClusterNodes.VetoReason`. If jobs stop running everywhere, check that
  the newest-stamped node is actually running. The stamp comes from `Update-Production.ps1`, so a
  deploy made any other way leaves a node unstamped and ungated.

- **A vending vendor is no longer a route customer. `/api/route-customers` reads take `scope`.**

  Route customers are the vans' shops — a van drives a round and the shops on it are its customers. A
  vending vendor sells from a cart out of a depot and is on no round at all: it has a depot, not a
  route. The two share one table, and every read of it used to answer with both, so the route customer
  list carried vendors grouped under their depots as if the depot were a route, and the van sales
  report counted vending takings while its own copy said "van sales only".

  `GET /api/route-customers`, `/sales-summary` and `/product-mix` now take `scope`: `route` (the
  **default**) is the vans' shops alone, `vending` the depots' vendors alone, `all` both. A vendor is
  decided the same way the vending overview decides it — a row under a business partner a `CartVendor`
  account sells on. **A caller that lists route customers without a scope no longer sees vendors**;
  pass `scope=vending` or `scope=all` to get them back. `scope` narrows and never widens: naming a
  depot in `assignedBusinessPartnerCode` under `scope=route` answers with nothing. The single-customer
  reads and every write are unchanged and take no scope, so vending still creates, edits and removes
  its vendors through this base route. The till's own vendor list
  (`GET /api/DesktopIntegration/.../vendors`) is unaffected.

  In the Web, a vendor's sales moved from `/route-customers/{id}/sales` to
  `/vending/vendors/{id}/sales`, under the Depots & vendors crumb; the old page no longer puts a
  depot in a breadcrumb labelled with a route.

- **Vendors at a vending depot must be coded VMB, VMP or VMM and three digits.**

  `POST /api/route-customers` under a business partner a `CartVendor` account sells on now holds the
  code to the depot's warehouse prefix — `VMB` for KEFBYC, `VMP` for KEFGRC, `VMM` for CORMACH — such as
  `VMB001`. A blank code used to be made from the vendor's name (`TENDAI`); it now takes the prefix's
  next number. Any other code is refused with `400 Vending.VendorCodeDoesNotFitDepot`, and a code held
  at another depot with `409 Vending.VendorCodeTaken`. A depot on a warehouse with no prefix (CORMACH2,
  say) refuses new vendors with `Vending.DepotCannotNumberVendors` until its cashiers are moved to one
  that has. `PUT /api/route-customers/{id}` checks the rule only when the code or depot changes, so
  existing vendors under older codes keep working and can still be edited. Van routes are unaffected.

  New alongside it: `POST /api/vending/vendors/import`, the bulk upload behind the Depots & vendors
  page, and `vendorCodePrefix`, `nextVendorCode` and `vendorCodeProblem` on each depot in
  `GET /api/vending/overview`.

- **Reading the van sales portal is now written to the audit trail.** Every write on `/api/van-sales`
  was already audited; none of the reads were, so a supervisor could pull any rep's takings, coverage
  or compliance for any period and nothing recorded who looked. All nine reads — the five reports,
  `routes`, `route-stops`, `visits` and `visits/report` — now write a row prefixed `VanSalesPortal`,
  with the query string, because the parameters are what say whose figures were read. No request or
  response changes.

- **The van sales customer app's sign-ins are now written to the audit trail.** Staff sign-ins have
  always written `Login` and `LoginFailed`; the customer app wrote nothing. An account that can place
  orders in a shop's name could be signed into, refused, sent codes on demand or handed a fresh token,
  and only the application log knew.

  All five `/api/van-sales-customer/auth` endpoints now write one row each, keyed on the account with
  the phone masked, plus `POST /api/van-sales-customer/devices`. Two rows are worth knowing about
  specifically: `VanSalesCustomerOtpRequest` records whether a code was *actually* sent, which the
  response deliberately does not say; and a failed `VanSalesCustomerSessionRefresh` with the error
  `Refresh token replay` is a rotated refresh token coming back — the strongest compromise signal
  the system produces.

  Also newly audited: handset assignment and offline-signing leases on `/api/fiscal-devices` (a forced
  handover over unsynced receipts is recorded as a failure), and push `register`, `unregister` and
  `send`. No request or response changes.

- **Every `/api/DesktopIntegration` write is now written to the audit trail.** The surface had no
  audit at all: 70 endpoints, of which only the transfer request and the by-hand SAP post recorded
  anything. A till could sell, invoice, cancel or retry a queued invoice, or consolidate the day's
  takings into SAP and leave nothing behind but the documents themselves.

  Nothing about the requests or responses changes. What changes is what an operator sees on
  `/audit-trail` and `/user-activity`: a row per desktop write, action prefixed `Desktop`, plus a
  fuller row from the handler for the sale, the two invoice paths, the queue cancel and retry, and
  the consolidation. Reads are deliberately left out — a till polls stock and queue state
  continuously — except `GET /sales` and `GET /end-of-day/report`, which show a shop's takings and
  are logged as `ViewDesktopSales` and `ViewDesktopEndOfDayReport`.

  The nightly `EndOfDayConsolidationJob` run is audited too, and its row carries no username,
  because no user raised it. Van sales is unaffected: `/api/vansales` audits reads as well as
  writes, as it always has.

- **Posting a desktop sale to SAP now takes a durable claim on the sale.** Every route that puts a
  till, vending or van sale in SAP — the two background passes, the exception centre's retry and the
  new manual and bulk levers — now acquires a cross-instance claim keyed on the sale's own external
  reference before anything reaches SAP.

  The existing guards made a *sequence* of attempts safe: ask SAP for `U_Van_saleorder` first, mark
  `PostIssuedAtUtc` before sending, adopt whatever is found. None of them says anything about two
  attempts overlapping, and both would pass their checks before either wrote anything. That was
  survivable while the only writer was one background pass; it stops being survivable now that a
  person can press Post while that pass is running, which at a pass a minute is roughly whenever it
  is pressed.

  Operators will see one new outcome: a post refused because another is already in flight. Nothing
  is written to the sale in that case and no attempt is spent — whoever holds the claim finishes,
  and the console shows the result on its next poll. A completed claim is remembered for
  `SecuritySettings:IdempotencyKeyExpirationMinutes` (an hour by default) and replays the invoice
  that was created; every failure gives the claim straight back, so a refused sale stays retryable.

- **`GET /api/DesktopIntegration/sales` reports where each sale got to on its way to SAP.** New
  fields: `sapDocEntry`, `sapDocNum`, `postedAt`, `postingAttempts`, `lastPostingError`,
  `paymentStatus`, `paymentSapDocNum`, plus `postRefusal` (null when the sale may be posted) and the
  derived `canPostToSap`. Existing fields are unchanged, so a client that ignores these is
  unaffected.

  `sapDocNum` and `postedAt` stay null for a sale the **end-of-day run** closed rather than the
  per-sale posting job: that run folds a customer's sales into one consolidated invoice and stamps
  the document number on the consolidation, so `consolidationId` is what names the invoice in that
  case. A reader that treats a null `sapDocNum` as "never reached SAP" will be wrong for exactly
  those rows.

- **`CreditNoteDto` carries `fiscalQrCode`.** The fiscal transaction row has always held the ZIMRA
  verification QR for a credit note's receipt; the projector had nowhere to put it, so credit note
  responses could say a receipt existed but never showed it. Invoices already carried theirs.

### Fixed

- **A till, vending or van sale's invoice PDF printed with no fiscal block at all.** No QR, no
  verification code, no fiscal day and no device — on a tax invoice whose customer is holding the
  receipt. It was intermittent by document rather than by day, so it read as the PDF design losing
  its fiscal block, and the design was rebuilt three times over it.

  The receipt is signed under the sale's own external reference, hours before SAP assigns a DocNum,
  while both places the PDF looked — the `DesktopFiscalTransactions` projection and the fiscal
  device read-back — are keyed on the DocNum. Neither could ever answer for a one-invoice-per-sale
  document, and the PDF drops the block silently when it has nothing to print.

  `GET /api/Invoice/{docEntry}/pdf` and `GET /api/DesktopIntegration/invoices/{docEntry}/pdf` now read the
  receipt off the sale row when a DocNum lookup finds nothing, so the block prints from the database
  and no longer depends on the device being reachable. An invoice recorded as fiscalised that still
  resolves no receipt is logged as a warning naming the document, instead of quietly printing
  without one. Re-downloading an affected invoice now yields its fiscal block; nothing needs
  reissuing.

- **A van sale that went through the invoice queue never became a SAP invoice.** That is every sales
  order a handset converted with `POST /vansales/order/convert-to-invoice`, which is always queued, and
  every online van sale made while SAP was unavailable. `InvoicePostingJob` fiscalised the entry and
  left it `Fiscalized`; the end-of-day consolidation posts every other such entry but has refused van
  sales since 2026-08-19, and nothing else picked them up. The handset had been told the conversion was
  accepted, the receipt was lodged with ZIMRA, and SAP held nothing.

  The same job now posts them, one invoice per sale, by confirming the reservation each was queued
  with — so the invoice carries the sale's own `U_Van_saleorder` and appears under **Van Sales
  Invoices**. It is never fiscalised again, and the invoice list reports it as fiscalised.

  A converted sale is invoiced against its sales order (`BaseType` 17), which is what the order link
  added the same day was for — that link lived only in the consolidation, which never sees a van sale.
  The rules are the consolidation's: only what the order still has open is linked, the sale's reserved
  batches are split with the line, a cancelled, closed or other customer's order is not linked, and an
  invoice SAP refuses while linked is posted once more without the link. A sale converted before its
  order reached SAP waits up to an hour for it, then posts unlinked.

  **Operators:** the first run after deploy works through the backlog, five sales per run. Each
  invoice is dated the day its sale was queued, not the day it posts, so back-dated A/R invoices will
  appear for dates since 2026-08-19. A sale whose posting period is closed, or whose batches have since
  been sold, is refused by SAP and lands in **Exception Center** as needing review rather than being
  retried. Such an entry is already fiscalised; Retry on it now asks the fiscal device before signing,
  and adopts the receipt it finds.

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
