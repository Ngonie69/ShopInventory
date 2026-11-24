# SAP-held transfers

How the app lists the inventory transfers and transfer requests that were keyed in the SAP B1 client,
held by SAP's own approval templates and approved there, and lets someone add one: the approved draft
becomes the real inventory transfer, or the real transfer request (see "Transfer request drafts" below).
Drafts still waiting for approval are listed too, and a request waiting at the caller's own SAP stage can
be decided here (see "Deciding from the page" below); every other stage is decided in the B1 client. SAP
is the source of truth throughout; nothing is mirrored into the app's local approval engine, which governs
the transfers this app raises itself.

**Why it exists.** Production holds 40 active transfer approval templates, each one SQL condition on
the source or destination warehouse with one approver stage. Approvers decide them in B1, but nobody
adds the approved drafts: on 9 Nov 2026, 519 approved transfer drafts were still open, dated back to
January 2024 (50 of them from 14 Sep – 8 Nov), so the stock they describe never moved in SAP. Count
them with `DocObjectCode eq '67'`: unfiltered, `StockTransferDrafts` also serves invoice, credit
note and goods issue drafts (1,337 approved drafts of every kind that day; the figure of 1,302 quoted
on 26 Oct counted all kinds the same way).

## The SAP entities

| What | Service Layer | Notes |
|---|---|---|
| The held document | `StockTransferDrafts` (EDM type `SAPB1.StockTransfer`) | Not `Drafts`, which is `Document`-shaped. `DocObjectCode = "67"` for a transfer, `"1250000001"` for a transfer request (not yet read back live); `AuthorizationStatus` is the transfer's own `sas*` enum (`sasPending → sasApproved`), not the documents' `das*` |
| The approval | `ApprovalRequests` | `ObjectType = '67'` (or `'1250000001'` for a request draft), `DraftEntry` = the draft's DocEntry. A draft can have more than one request — one per template that matched it |
| The add | `POST StockTransferDrafts({DocEntry})/SaveDraftToDocument` | The bound action declared at `reference/sap-service-layer-metadata.xml` (around line 24174), sent with no body |
| Who decides | the B1 client | This page never decides; a draft appears only once SAP has approved it |

The select lists are in `ShopInventory/Services/SAPServiceLayerClient.TransferApprovals.cs`, checked
against the metadata by `SapSelectClauseTests` and against a live Service Layer by
`ShopInventory.IntegrationTests/SapTransferApprovalReadTests.cs` (`SHOPINVENTORY_SAP_TESTS=1`).

## What the reads settled

Measured against `KEFALOS_TEST_3` on 2026-11-06, reads only. The test company is a copy that ends in
December 2023, so its numbers describe that copy, not production.

| Question | Answer |
|---|---|
| How many transfer approval requests, and in what state? | 19,359 with `ObjectType '67'`: 18,625 `arsApproved`, 401 `arsPending`, 333 `arsNotApproved`, none generated or cancelled |
| What does the B1 client leave behind when it adds an approved draft? | The request **stays** `arsApproved`, with `IsDraft` flipped to `N` and `ObjectEntry` set to the new transfer. The draft goes `bost_Close` / `sasWithout`; the transfer is stamped `sasGeneratedbyAuthorizer`. Its DocNum is not the draft's |
| How many approved, never added? | 157 requests with `IsDraft 'Y'`; 154 of their drafts still exist and are open — 144 `sasApproved`, 8 `sasPending`, 2 `sasRejected` (SAP approved the request and left the draft behind, as it once did for a credit memo) |
| Does the draft side agree? | **Yes, exactly.** `StockTransferDrafts` filtered on `DocObjectCode '67'`, `bost_Open` and `sasApproved` counts 144, and every one has an approved request (three have two) |
| Is `StockTransferDrafts` only transfers? | **No.** It serves every draft; an unfiltered page returned goods-receipt-PO drafts (`DocObjectCode "20"`) first. Every read filters on the object code |
| Do the batch picks come back on the lines? | **Only from a read that returns one document with no `$select`.** A filtered read of several drafts, or any read whose `$select` names `StockTransferLines`, returns every line with an empty `BatchNumbers` — every batch-managed draft would look unallocated. The list therefore reads headers only, and the detail and the add read the draft by key |
| How many approved drafts lack a pick? | Of the 154 open drafts: 124 have every batch-managed line fully picked, 21 hold no batch-managed item, and 9 have a batch-managed line with no or a partial pick |
| Can a draft be traced to the transfer request it was copied from? | **Yes.** Its lines carry `BaseType "InventoryTransferRequest"` and `BaseEntry` = the request's DocEntry. Request 22788 (#10688), behind draft 78464, is still open with most of its lines closed by other transfers |

## Who may see, decide and add

The roles are the ones that may convert a transfer request (`SapTransferApprovalQueueTests` holds the two
lists together). Which drafts each sees, decides and adds is one rule, `SapTransferApprovalCaller`
(resolved by `SapTransferApprovalAccess`), asked by the list, the drawer, the decision and the add:

| Role | Sees | Decides and adds |
|---|---|---|
| Admin, StockController | every draft | every draft |
| DepotController | drafts whose every source — header and each line — is one of the warehouses on the account | the same |
| ShopStock | every draft | leaving **or entering** one of the warehouses on the account, header or line |
| WashBay | every draft | leaving **or entering** `WASHBAY`, header or line (a WashBay account holds no assignments) |

**Why (the user):** 2026-11-23, "washbay and shopstock should be able to view SAP held transfers and be
able to approve drafts and add, for the warehouse they are linked to"; 2026-11-24, "shopstock and washbay
should be able to see all warehouse under sap held transfers". What they approve and add is mostly coming
in — PROYG 2 → WASHBAY, Dispatch → KEFSHOP — which the source-only rule never allowed a ShopStock user.
Until then the wash bay could add every warehouse's drafts; it now adds only WASHBAY's. Converting a
request on the Transfers page is unchanged: a conversion that reaches the SAP-held add has already asked
`EnsureCanConvertRequestAsync` on its request and passes `CallerAuthorized`, so the wash bay still converts
from any warehouse there.

Seeing is not acting: a WashBay or ShopStock row outside their warehouses has `canAdd: false` and a status
note saying it neither leaves nor enters one of theirs, and its drawer offers no decision, with the same
sentence as the request's `decideNote`. The decision and the add check again (`AdmitsDraft`), so the API
refuses what the page does not offer.

The role is read from the account by user id, never from the request's role claims: the Web calls the
API with its integration key and the user's token together, so those claims include the key's Admin.
A depot controller's queue is filtered — and counted — in SAP on the draft header's `FromWarehouse`.

## What happens on the add

1. **One add per draft**, whoever clicks: the claim (`sap-transfer-draft-add`, keyed on the draft) is
   taken first; a repeat replays the first answer, checked again against the caller's warehouses.
2. The draft is read by key. A closed one has been added already (here or in B1): the answer is
   `AlreadyAdded`, naming the transfer when a request's `ObjectEntry` does, and any copied-from request
   still open. Not `sasApproved`, or a request on it still pending: refused.
3. **Batch picks.** SAP refuses the whole transfer for one batch- or serial-managed line whose picks do
   not cover it (`Cannot add row without complete selection of batch/serial numbers`, as it did for a
   credit memo draft). The add checks first and says which lines; when the item flags cannot be read,
   SAP's own refusal is turned into the same instruction: pick the batches on the draft in the B1 client.
4. **Copied from a transfer request.** The request is held under the conversion lock
   (`transfer-request-conversion-post`, the one `ConvertTransferRequestHandler` uses), and the add is
   refused when this app already converted it — its transfer carries `ITR-{DocEntry}` — because that
   transfer already moved the stock.
5. **Unposted till sales.** The conversion's check: a transfer out of a till's warehouse must not take
   units a till sold that SAP has not invoiced yet.
6. `SaveDraftToDocument` on a token the caller cannot cancel. A call whose answer is lost is settled by
   reading the draft back: closed means it landed; still open is `AddUncertain`, and the claim is
   released so the next click asks again.
7. **The transfer it became** is SAP's answer when it names one, else a request's `ObjectEntry`, else
   the one transfer posted since the add began between the same warehouses carrying exactly the draft's
   lines. Two identical candidates are left unresolved rather than guessed between. Never DocNum.
8. **The copied-from request is closed** if it is still open, and the app's own approval of it, if it
   has one, is marked generated. A draft copied from a request does not close it, and an open request
   could be converted here and move the same stock again. A request that will not close is said in the
   result, not thrown: the stock has moved. Lines of the request the B1 user left off the draft are not
   transferred; raise a new request for them.

## Transfer request drafts

`InventoryTransferRequests` and `StockTransferDrafts` share the `StockTransfer` entity type, and a
transfer request keyed in the B1 client under an approval template waits in the same draft set as the
transfers, under its own object code. The page reads both: a switch above the queue picks **All**
(the default), **Transfers** or **Requests**, each showing its total, and every row and the drawer say
which kind it is. The switch is not a filter, so the search and filters carry over and "Clear all"
leaves it alone.

The list counts the kind it reads (both together when it reads both) and makes one more `$count` of a
single kind; the other total is the difference, clamped at zero because an add can land between the two
reads. A count that fails leaves the switch without numbers; the rows are unaffected.

What changes for a request draft:

- **No stock moves when it is added.** `SaveDraftToDocument` makes an open transfer request, which waits
  on the Transfers page (Requests tab) to be converted like any other — through the app's own approval
  (§44), which opens one for a request raised in SAP the first time it is converted. **Both approvals
  stay, by decision (2026-11-11):** SAP's approval lets the request exist; the app's decides the
  conversion, as it does for every request. Do not skip the app's approval for a request added here.
- **No stock checks.** A request carries no batch picks (they are chosen at conversion), was copied from
  no request, and cannot take a till's unposted sales, so the detail reads no item flags and the add
  skips the pick, copied-from-request and till checks. Who may add is unchanged: the conversion rule, on
  the warehouse the request asks stock from.
- **The request it became** is SAP's answer when it names one, else the approval request's
  `ObjectEntry`, else the one request raised since the add began between the same warehouses carrying
  exactly the draft's lines — the transfer's recognition, on `InventoryTransferRequests`.
- The page's notice links to the Transfers page opened on that request
  (`/inventory-transfers?requestDocEntry=…`).

**2026-11-12:** `The_request_draft_queue_is_accepted_and_holds_only_request_drafts` and
`Both_kinds_count_as_the_sum_of_each` passed against `KEFALOS_TEST_3`, so SAP accepts the object code and
the counts add up. An add of a request draft is still unmeasured.

**Unmeasured (as written 2026-11-11).** None of this has been read from a live request draft: the test company's login was
refused when it was built (2026-11-11). Before relying on it, run
`ShopInventory.IntegrationTests` with `SHOPINVENTORY_SAP_TESTS=1` — the request-draft reads settle the
object code (`The_request_draft_queue_is_accepted_and_holds_only_request_drafts`) and that the two
counts add up — and record here how many approved request drafts there are, and what one add answers.

Laid out from the "SAP-Held Transfer Requests" design canvas (November 2026).

## Depot and factory transfers (2026-11-16)

Asked for 2026-11-16: transfers split into two types. **Depot transfers** keep the app's own approval
templates and stages and post to SAP once approved, as before. **Factory transfers** use SAP's approval
templates only, as a transfer keyed in the B1 client would: the app's approval does not apply to them,
the approval is triggered in the B1 client, and once SAP has approved it the person in the app sees it
approved and adds it. This replaces the 2026-11-12 decision below to keep both approvals on conversions.

**Which type** (`Features/InventoryTransfers/TransferTypes`, the same rule as
`SapTransferApprovalRoutes.ExcludedWarehouse`): any warehouse on the transfer, header or line, that is a
depot centre or shop (`FactoryDepartments.Depots`) or a van makes it a depot transfer. Everything else
is a factory transfer — the factory shop `KEFSHOP` included (user's call): Dispatch ↔ Factory Shop
follows SAP's approvals, and the app's "Dispatch to Factory Shop" / "Factory Shop returns to Dispatch"
templates of 5 Nov no longer hold anything. Request edits (`InventoryTransferRequestEdit`) keep the app's
approval whatever the route: who may change someone else's request is a different control.

**The app's approval of a factory transfer** opens settled: `ApprovalRequests` row with template
`SAP approval (factory transfer)` (template id `22000000-…-00fa`, which no definition carries), no
stages, status `Approved`, nobody alerted. It is kept rather than skipped so the originator stays on file
(the "on its way" message on conversion) and the conversion marks it generated. A change that keeps it
a factory transfer leaves it settled; a change that puts a depot or van on it routes it through the
app's templates like a new depot transfer.

**Direct transfers** (`FactoryTransferSapApproval`). On submission, and on each later press of the
row's SAP button (`POST .../pending/{id}/post`, labelled Send to SAP / Check SAP / Add in SAP):

1. A transfer in SAP carrying the draft number in `U_OrderNumber` — added in the B1 client, on SAP-Held
   Transfers, or by a step whose answer was lost — is recorded as the post.
2. An open transfer draft carrying it says where SAP's approval stands: `sasWithout` → the B1 user has
   not added it yet (`AwaitingSapClient`); `sasPending` → with SAP's approver; `sasRejected` → the
   transfer is `Rejected`; `sasApproved` → added through the SAP-held add (batch picks, till sales,
   recognition), with the warehouse check already made by the caller (`CallerAuthorized`).
3. Otherwise SAP's templates are read for the header route: covered → saved as a draft
   (`CreateStockTransferDraftAsync`, remarks "Reason | Requested by"), status `HeldInSap`; not covered →
   posted at once by the ordinary poster.

One step runs per transfer at a time (`factory-transfer-sap-step`). A draft closed without a transfer
was removed in B1; the next step saves a new one. Nothing refreshes the state on its own: the list shows
what the last step read (`sapCheckedAtUtc`), and adding the draft from SAP-Held Transfers or in B1 is
picked up by the next press.

**Telling SAP's approvers (2026-11-19).** Saving the draft does not start SAP's approval: re-tested on
`KEFALOS_TEST_3` on 2026-11-19, draft 80193 (DT-2026-V0003, WAREH → PROYG 2) sat at `sasWithout` with no
approval request until a B1 user added it (16 Nov's request 74432 was opened by an Add pressed in the B1
client). So whenever a draft is first saved, by a direct transfer or a request conversion,
`SapDraftApprovalNotice` puts a message in the B1 inbox (Messages/Alerts Overview, `POST Messages`, one
attempt) of every approver on the matched templates' stages (`ApprovalTemplateStages` →
`ApprovalStages(n).ApprovalStageApprovers` → `Users`). Its "Draft" column links to the draft (object 112,
keyed by `DocEntry`), and the text names the order number, because draft numbers repeat. The approver opens it and presses Add,
and SAP then raises its approval. Later presses never send it again. A failed message does not fail the
step: the reply to the person says who was told, or that nobody was. An approver missing from the
template's originators (`ApprovalTemplateUsers`) is still messaged and is logged: if they add the draft,
SAP posts it with no approval.

**Transfer requests.** Converting a factory request skips the app's approval entirely, including a
stage still open on a request raised before the split, and goes straight to the conversion below
(marker lookup, SAP template, draft / wait / add, or post).

**The draft write, run once on `KEFALOS_TEST_3` (2026-11-16, user-approved)** — the real
`FactoryTransferSapApproval` and Service Layer client, the held transfer in memory only (temporary test
`SapFactoryTransferOnce`, removed after the run; copy in `artifacts/verify/20261116-factory-transfers/`):

- WAREH → PROYG 2, 1 × PCH007 and 1 × PYG093, dated 2026-10-20 (the test company has no rate after
  24 Oct), under "Trans From Warehouse": press 1 saved draft **DocEntry 80191 / DocNum 112390**,
  `bost_Open`, `sasWithout`, `DocObjectCode 67`, `U_OrderNumber DT-2026-V0001`, remarks "Reason: … |
  Requested by …", both lines as sent with their units. Press 2 found it and answered "waiting to be
  added in the SAP client"; exactly one draft carries the marker. Nothing moved.
- **SAP's draft numbers repeat:** 80191 and PR #907's 80189 both read DocNum 112390. The app's messages
  therefore name the order number (the draft number, `DT-…`) beside the DocNum, and the Web tells a B1
  user to go by it.
- Draft 80191 is left in the test company for the next proof: add it in the B1 client as a user on the
  "Trans From Warehouse" originator list, approve it, then press Check SAP — which adds it (moving 1
  PCH007 and 1 PYG093 WAREH → PROYG 2 in the test company).

**Still unproven:** the same as below — that the B1 client's add of an app-made draft starts SAP's
approval, and the app's add of the approved draft. Until that is seen on the test company, a factory
transfer on a covered route can only be moved on by adding its draft in B1.

## Conversions on routes SAP approves

Asked for 2026-11-12: converting a transfer request must respect SAP's own approval where SAP has one,
and convert only once SAP has approved it — except depots and van sales. The user chose, of three
designs, to send the transfer to SAP as a draft, and to keep the app's own approval as well.

**Why a draft and a B1 step.** SAP's templates never run for a document that arrives over the Service
Layer (tested 2026-11-06, with "Enable Approval Process in DI" off and on: every transfer template's
condition is a B1 screen query on `$[OWTR.…]`), and a draft posted over the Service Layer never opens
an approval request either. SAP holds a transfer only when someone *adds* it in the B1 client. So the
conversion makes the draft, and a B1 user adds it.

**What decides "a route SAP approves"** — `SapTransferApprovalRoutes`:

1. Any warehouse on the request (header or line) is a depot centre or shop (`FactoryDepartments.Depots`:
   KEFGRC/KEFGRS, CORMACH/CORMACH2, KEFBYC/KEFBYS) or a van (`VAN` and a number): excluded, converts as
   before, and SAP is not read.
2. Otherwise the active `ApprovalTemplates` on `atdtInventoryTransfer` are read with their user queries
   (`UserQueries`), kept 10 minutes, the last good read used for 12 hours if a refresh fails. A template
   holds the route when it has no conditions (`UseTerms` tNO, SAP's "Always"), or when any one of its
   queries holds for the request's header From/To. Originator lists are not read: who will add the
   draft in B1 is not known.
3. `SapTransferTemplateCondition` reads a query's WHERE clause — `=`, `<>`, `LIKE`/`NOT LIKE`, `IN`,
   `AND`/`OR`/`NOT`, brackets — over `Filler` (From) and `ToWhsCode` (To), each as `T0."x"` or
   `$[OWTR.x]`. Anything else (another column, a join, a function, plain terms on totals) is
   *unreadable*: logged, and it never sends a conversion to SAP.

Checked live against `KEFALOS_TEST_3` on 2026-11-12 (`ShopInventory.IntegrationTests`, read-only):
every active transfer template there is readable, and SAP's own decisions agree — of the 84 approved
drafts held by a still-active template, 81 are on a route the app says that template covers; the other
3 are 2022 drafts of template #69 "CMFS Stock Transfers", which held transfers *into* CMFS until Oct 2022
and has held only transfers *out of* CMFS since (so the test compares the last year only).
**Production (`KEFALOS_USD_NEW2`), read 2026-11-12, reads only.** Both of those tests pass with
`SAP__CompanyDB` set to it. Read through the app's own client and parser:

- 83 approval templates, 40 of them active on inventory transfers. Every one of the 40 is the
  one-query shape above (template #59 "Graniteside Centre" has two queries), and all 40 are readable.
- SAP's own decisions: 3,894 approval requests on transfers were created from 1 Oct 2026. The 3,796
  under a template still active are all on a route the app says that template holds; the other 98
  were under templates since switched off.
- What Convert now does with the newest 1,000 requests (30 Sep – 11 Nov 2026): 676 touch a depot or
  a van and convert as before, 3 are on no template's route, and **321 go to SAP as a draft**, about
  7 or 8 a day. Of those 321: WAREH to the production warehouses 192 ("Trans From Warehouse" with
  "Prod transfers", "Icecream Approval" or "Transfers to Yoghurt"), WASHBAY or DISPYOG to PROYG 2 53,
  CMB to CMFS 30, into the dispatch warehouses 37, and 9 others.
- The open queue holds more than 3,000 requests, back to June 2025; of the newest 3,000, 1,494 are on
  a covered route.

**What a conversion does** (`ConvertTransferRequestHandler`, after the app's approval is complete and
the `ITR-{DocEntry}` transfer lookup found nothing):

| SAP draft carrying `ITR-{DocEntry}` | Answer |
|---|---|
| none open | saves one (`POST StockTransferDrafts`, `DocObjectCode 67`, the same lines, batch picks and remarks posting would send, plus each line's `UoMEntry` — see below) — `sapApproval: Saved` |
| open, `sasWithout` | `AwaitingSapClient`: add it in the B1 client |
| open, `sasPending` | `Pending` |
| open, `sasApproved` | adds it through the SAP-held add below — `Added` |
| open, `sasRejected` | 409 `InventoryTransfer.SapApprovalRejected` |

A transfer that already carries the marker — SAP approved the draft and its originator added it in B1 —
is found first and closes the request, as for any earlier conversion. The SAP-held add treats a
transfer draft carrying `ITR-{n}` as made from request *n* (`LinkedRequestEntries`), so adding it from
SAP-Held Transfers closes that request and marks the app's approval generated, as for a draft whose
lines B1 copied from the request. A waiting answer is never stored for replay, so the next press asks
SAP again.

**The draft write, run once on `KEFALOS_TEST_3` (2026-11-12)** — the real handler and Service Layer
client, the app's approval stood in as complete:

- A WAREH → PROYG 2 request under the "Trans From Warehouse" template (4 lines, one batch-managed)
  became a draft: `bost_Open`, `sasWithout`, `DocObjectCode 67`, `U_OrderNumber ITR-{DocEntry}`, every
  line's item, quantity, warehouses and unit as on the request, and the batch line picked from a batch
  at the source. The request stayed open and nothing moved. Pressing Convert again answered
  `AwaitingSapClient` with the same draft, and the marker finds exactly one.
- The first attempt was refused, writing nothing: **a draft line must name its unit.** SAP answered
  `1470000315 Cannot add or update document; specify a UoM code` to lines that post fine with none.
  Draft lines now carry the item's inventory `UoMEntry` (what a posted line counts in when it names
  none); posted transfers are unchanged. Two of the four items have no default sales unit, which may be
  what SAP fell back to for a draft — not tested.
- A CMFS → CMB request was refused before any write: the test company holds no CMFS batches of
  its items.

**Not yet proven:** that adding the app's draft in the B1 client starts SAP's approval for it (the B1
user must be on the template's originator list; in production about 1,250 of 6,644 transfers between
14 Sep and 8 Nov matched a template but were keyed by someone not on it, and went through unheld), and
whether SAP keeps the same draft or makes a copy (the marker lookup handles either). The run's draft on
`KEFALOS_TEST_3` is waiting for exactly this: add it in the B1 client as a user on the
"Trans From Warehouse" template's originator list.

## Deciding from the page (2026-11-23)

Asked for by the user: the wash bay should see every draft going to WASHBAY and approve them.

- **The waiting view** is the same read with `AuthorizationStatus eq 'sasPending'` instead of
  `'sasApproved'` (`SapTransferDraftFilter.AwaitingApproval`), so every filter, count and the kind switch
  carry over. The wash bay opens on it with *To WASHBAY* set, a ShopStock user opens on it too; everyone else opens
  on the approved view.
- **The decision** is `PATCH ApprovalRequests(code)` as the app's SAP approver, exactly the credit memo
  decision (`DecideSapTransferApprovalHandler` reuses its remarks, stage scope and approver checks). The
  caller needs `transfers.approve_sap` (Admin, WashBay, and ShopStock since 2026-11-23); which stages they
  decide is `CreditNoteApprovals:RoleStageScopes` — for WashBay, `Factory Washbay`, the stage its credit
  memos already wait at. ShopStock has no stage configured, so it is narrowed by its warehouses alone
  (either end, above) and by whether SAP lists the app's approver (`Software`) on the stage. **Not checked
  in production:** which stage holds transfers into KEFSHOP, and whether `Software` is listed on it. Where
  it is not, the drawer says so and offers no decision, and the shop decides in the B1 client. A ShopStock
  user opens on the waiting view too, across every warehouse, with no destination chip.
  After an approval the **draft** is read back: `canAdd` only when it is `sasApproved`, because a
  draft held by a second template stays `sasPending` until that one is decided too.
- **Why the decision should work for transfers.** Read-only, production, 2026-11-23: since 1 Oct the app
  made 75 credit memo decisions at stage 44 as SAP user `Software`, and their drafts flipped
  (`dasApproved`, then `dasWithout` once added) as a B1 decision does. Template 91 "Washbay Transfers"
  holds transfers at the same stage 44, whose approvers are Opsassist, PdnAdmin, Finacc, Software and
  Washbay — so `Software` may decide them. **Not yet proven:** no transfer request has been decided through
  the Service Layer; the first wash bay approval is the first one. Check its draft read back `sasApproved`.
- **What else holds drafts going to WASHBAY** (130 pending on 2026-11-23, back to 2025-09-17). Each is the
  sending side's approval, where SAP does not list `Software`, so the drawer names the stage and offers
  no decision:

  | Stage | Template | Pending | Raised since 1 Nov |
  |---|---|---:|---:|
  | 44 Factory Washbay | 91 Washbay Transfers | 15 | 15 |
  | 30 Bulawayo Depot | 70 Transfers from CMB | 20 | 10 |
  | 24 Graniteside | 59 Graniteside Centre | 10 | 0 |
  | 22 Transfers to Returns | 49 Transfer from Return | 7 | 1 |
  | 8 Dispatch Warehouses | 64 Transfer from CMFCs | 4 | 0 |
  | 8 Dispatch Warehouses | 75 Transfes To Washbay (**inactive**) | 102 | 7 |

  Template 75 is inactive now, but its 102 requests stay pending, and their drafts stay in the wash bay's
  view, until somebody approves or removes them in B1.

## The add has not been run against SAP

The write was not exercised: the session that built this was not permitted to post to the test
company. What is proven is the reads above (live), the handler's behaviour against a stand-in Service
Layer (`SapTransferApprovalAddTests`), and the page against the live reads. Before production, run one
add in `KEFALOS_TEST_3` and record here:

- what `SaveDraftToDocument` answers (204, or a body naming the transfer);
- whether the request is deleted, as for a credit memo, or kept with `ObjectEntry`, as the B1 client
  keeps it;
- whether the draft goes `bost_Close` (the recovery path relies on it);
- what SAP says for draft 61875 (request 56603), whose one batch-managed line has no pick — the add
  refuses it before SAP, so send that one by hand;
- whether a draft copied from a request (78464 → request 22788) leaves the request open.

Candidates on 2026-11-06: draft 79744 (request 74045, CMFC → DISPYOG, one line of YOG058, 8 units, batch
picked and released, 1,271 on hand).

## The page

`/inventory-transfers/sap-approvals` (Web), for `UserRoles.SapTransferApprovalRoles` — the transfer roles.
Nocturne, generated from the credit memo queue's sheet by `scripts/derive_sap_transfer_approvals_css.py`
under the `sta-` prefix; edit the script, never the sheet. The routes are API.md §50a.

Laid out from the "SAP-Held Transfers Redesign" design canvas (November 2026): rows grouped by the day the
draft was created, the route as each row's headline, a Review drawer that confirms the add in its own
footer, and a numbered pager. The search box takes a warehouse code (leaving or entering) or a draft
number. The Filters panel offers source and destination warehouses (several each), a "Raised" window and
"Has attachment"; every one is a clause on the draft header (`SAPServiceLayerClient.DraftQueueFilterClauses`),
so SAP filters and counts. Two filters the design drew are not offered: the approval stage and the
originator live on the approval request, not the draft, and SAP cannot filter one entity set by another's
fields in a single read. A Ready/Held split is not counted either: the queue is already the approved, open
drafts, so a held row is rare and only the row itself can say so. (For the wash bay and a shop's stock
user, who see every warehouse, a row outside their own says so the same way.)
