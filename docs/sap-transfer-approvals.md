# SAP-held transfers

How the app lists the inventory transfers and transfer requests that were keyed in the SAP B1 client,
held by SAP's own approval templates and approved there, and lets someone add one: the approved draft
becomes the real inventory transfer, or the real transfer request (see "Transfer request drafts" below).
Deciding stays in the B1 client. SAP is the source of truth throughout; nothing is
mirrored into the app's local approval engine, which governs the transfers this app raises itself.

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

## Who may add

The people who may convert a transfer request, through the same
`ITransferWarehouseAuthorizer.EnsureCanConvertRequestAsync`: Admin, StockController and WashBay from any
warehouse; DepotController and ShopStock only from the warehouses on their account. The add asks it for
every warehouse the draft takes stock out of — its header and each line — because a line can name its
own source.

The role is read from the account by user id, never from the request's role claims: the Web calls the
API with its integration key and the user's token together, so those claims include the key's Admin.
A scoped account's queue is filtered — and counted — in SAP on the draft's source warehouse.
`SapTransferApprovalQueueTests` holds the queue's roles to the conversion's.

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
**Production's templates have not been read**; run
`Every_active_transfer_template_condition_can_be_answered_for_a_route` and
`Every_transfer_SAP_held_is_on_a_route_its_template_covers_here` with `SAP__CompanyDB` set to it first.

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

**Not yet proven, in order:**

1. That adding the app's draft in the B1 client starts SAP's approval for it (the B1 user must be on the
   template's originator list; in production about 1,250 of 6,644 transfers between 14 Sep and 8 Nov
   matched a template but were keyed by someone not on it, and went through unheld), and whether
   SAP keeps the same draft or makes a copy (the marker lookup handles either). The run's draft on
   `KEFALOS_TEST_3` is waiting for exactly this: add it in the B1 client as a user on the
   "Trans From Warehouse" template's originator list.
2. Production's templates, as above.

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
drafts in the caller's warehouses, so a held row is rare and only the row itself can say so.
