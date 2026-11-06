# SAP-held transfers

How the app lists the inventory transfers that were keyed in the SAP B1 client, held by SAP's own
approval templates and approved there, and lets someone add one: the approved draft becomes the real
inventory transfer. Deciding stays in the B1 client. SAP is the source of truth throughout; nothing is
mirrored into the app's local approval engine, which governs the transfers this app raises itself.

**Why it exists.** Production holds 40 active transfer approval templates, each one SQL condition on
the source or destination warehouse with one approver stage. Approvers decide them in B1, but nobody
adds the approved drafts: about 1,302 approved transfer drafts went unadded between 31 Aug and
26 Oct 2026, so the stock they describe never moved in SAP.

## The SAP entities

| What | Service Layer | Notes |
|---|---|---|
| The held document | `StockTransferDrafts` (EDM type `SAPB1.StockTransfer`) | Not `Drafts`, which is `Document`-shaped. `DocObjectCode = "67"`; `AuthorizationStatus` is the transfer's own `sas*` enum (`sasPending → sasApproved`), not the documents' `das*` |
| The approval | `ApprovalRequests` | `ObjectType = '67'`, `DraftEntry` = the draft's DocEntry. A draft can have more than one request — one per template that matched it |
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
   not cover it, **and for a pick whose batch has since been used up** — both with the same
   `-4014 Cannot add row without complete selection of batch/serial numbers` (see the first run below).
   The add checks both first and says which line or which batch: a missing pick from the item flags, a
   used-up one from what each picked batch holds now in the line's source warehouse, read through the
   fixed `ITEM_BATCHES_V1` query (one read per item and warehouse, three at a time; it mints no
   SQLQueries object). Picks of one batch are summed across lines. When either read fails, SAP decides,
   and its refusal is turned into an instruction that names both causes.
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

## The first add against SAP

Run in `KEFALOS_TEST_3` on 2026-11-06 with the user's go-ahead: draft **79744** (request 74045, CMFC →
DISPYOG, one line of YOG058, 8 units, batch `YOG058#M11/23/N` picked for 8, header and line approved).

| | before | after |
|---|---|---|
| `POST StockTransferDrafts(79744)/SaveDraftToDocument` (no body) | | **400** `-4014 Cannot add row without complete selection of batch/serial numbers`, in 2.1 s |
| `ApprovalRequests(74045)` | `arsApproved`, `IsDraft Y`, no `ObjectEntry` | unchanged |
| `StockTransferDrafts(79744)` | `bost_Open`, `sasApproved` | unchanged |
| Newest stock transfer | DocEntry 151574 | unchanged — nothing was posted |

**Why SAP refused.** The line *was* picked. The batch it picked no longer holds anything at CMFC: read
back through `ITEM_BATCHES_V1`, YOG058 at CMFC holds only `YOG058#M20/23 N` (162) and `YOG058#M27/23 N`
(1,109). SAP answers a used-up pick with exactly the words it uses for no pick at all. Production's
approved drafts are weeks old and the factory issues batches first in, first out, so this will be the
common refusal; the page now finds it before the add and names the batch (`PickedBatchUsedUp`).

**Still not measured**, because the refusal happened before anything was written: what a successful
`SaveDraftToDocument` answers (204, or a body naming the transfer), whether it keeps the request with
`ObjectEntry` as the B1 client does or deletes it as the credit memo add does, whether the draft goes
`bost_Close`, and whether a draft copied from a request leaves that request open. The code handles each
either way — it names the transfer from SAP's answer, a request's `ObjectEntry` or the draft's lines, and
settles a lost answer by the draft — but a successful run should confirm them. It needs a draft whose
picked batches all still hold their quantities; the detail page now says which drafts those are.

A read-only survey for such a draft was cut short when the Service Layer stopped answering from this
machine (TCP connect timeouts at 10.10.10.6:50000).

## The page

`/inventory-transfers/sap-approvals` (Web), for `UserRoles.SapTransferApprovalRoles` — the transfer roles.
Nocturne, generated from the credit memo queue's sheet by `scripts/derive_sap_transfer_approvals_css.py`
under the `sta-` prefix; edit the script, never the sheet. The routes are API.md §50a.
