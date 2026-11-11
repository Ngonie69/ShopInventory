# SAP credit note approvals

How the app lets a person approve an A/R credit memo that was raised in the SAP B1 client and held by
SAP's own approval procedure, add the approved draft as the real credit note, and view the file attached
to the draft. SAP is the source of truth throughout: nothing here is mirrored into the app's local
approval engine, which governs the documents this app posts itself.

## The SAP entities

| What | Service Layer | Notes |
|---|---|---|
| The held document | `Drafts` (EDM type `SAPB1.Document`) | `DocObjectCode = 'oCreditNotes'`; `AuthorizationStatus` runs `dasPending → dasApproved → dasGenerated`; `AttachmentEntry` names its files |
| The approval | `ApprovalRequests` (key `Code`) | `ObjectType = '14'`, `DraftEntry` = the draft's DocEntry, `Status` in `arsPending / arsApproved / arsNotApproved / arsGenerated / arsGeneratedByAuthorizer / arsCancelled`, `CurrentStage`, `OriginatorID`, `ApprovalRequestLines[]` one row per approver per stage |
| A decision | `PATCH ApprovalRequests(code)` | body `{"ApprovalRequestDecisions":[{"ApproverUserName":…,"ApproverPassword":…,"Status":"ardApproved" or "ardNotApproved","Remarks":…}]}` |
| The add | `POST DraftsService_SaveDraftToDocument` | body `{"Document":{"DocEntry":<draftEntry>}}` — an unbound operation; this Service Layer declares no `Drafts(x)/SaveDraftToDocument` |
| Who may decide | `ApprovalStages(code).ApprovalStageApprovers[].UserID` | matched against `Users(InternalKey)` |
| The files | `Attachments2(entry)` and `Attachments2(entry)/$value?filename='name.ext'` | one line per file: `FileName`, `FileExtension`, `AttachmentDate` |

The select lists the client sends are in `ShopInventory/Services/SAPServiceLayerClient.Approvals.cs` and
are checked against `reference/sap-service-layer-metadata.xml` by `SapSelectClauseTests`. The metadata
cannot prove that the `Drafts` set accepts them at runtime; `ShopInventory.IntegrationTests/
SapCreditNoteApprovalReadTests.cs` is what settles that, on the SAP network.

## The service approver

SAP records a decision against a user it lists as an approver on the request's current stage. The app
decides as one **service approver**: `SAP:ApprovalApproverUsername` / `SAP:ApprovalApproverPassword`,
defaulting to the session account `SAP:Username` / `SAP:Password`. The app's own `creditnotes.approve`
permission decides who may click, the decision remarks name that person
(`Approved in ShopInventory by ngoni: …`), and the app's audit trail records them; SAP's own record shows
the service account.

**Prerequisite in SAP:** the service approver must be listed as an approver on every stage of every
approval template that covers A/R credit memos (`ApprovalTemplateDocuments.DocumentType = atdtArCreditMemo`).
The app checks this before it asks SAP — a request whose stage does not list the user shows
"SAP stage 'X' does not list manager as an approver" instead of a SAP error — and it can list what to
fix with:

```bash
SID=<session id>; B=https://10.10.10.6:50000/b1s/v1
curl -sk -H "Cookie: B1SESSION=$SID" -H "Prefer: odata.maxpagesize=100" "$B/ApprovalTemplates?\$select=Code,Name,IsActive,ApprovalTemplateDocuments,ApprovalTemplateStages"
curl -sk -H "Cookie: B1SESSION=$SID" -H "Prefer: odata.maxpagesize=100" "$B/ApprovalStages?\$select=Code,Name,NoOfApproversRequired,ApprovalStageApprovers"
curl -sk -H "Cookie: B1SESSION=$SID" "$B/Users?\$filter=UserCode%20eq%20'manager'&\$select=InternalKey,UserCode,UserName"
```

## Configuration

| Key | Default | Meaning |
|---|---|---|
| `SAP:ApprovalApproverUsername` | `SAP:Username` | The SAP user decisions are recorded as |
| `SAP:ApprovalApproverPassword` | `SAP:Password` when the approver is the session user, else omitted from the payload | Its password; never logged |
| `CreditNoteApprovals:FiscaliseAfterAdd` | `true` | Fiscalise the credit note right after the add. A document added through the Service Layer never passes the fiscalisation platform's B1 print bridge, so with this off it is fiscalised only when somebody next prints it in the B1 client |
| `CreditNoteApprovals:RoleStageScopes` | `WashBay` → `Factory Washbay` in `appsettings.json`; empty if absent | Role → the SAP stage names it may read and decide; see "Stage scopes" below |
| `CreditNoteApprovals:AttachmentReadMode` | `Share` in `appsettings.json`; `ServiceLayer` if the key is absent | `Share` reads the file off `SAP:AttachmentsPath` with the share credentials instead of streaming `$value`, for a Service Layer that cannot serve the folder — which is this landscape, see below |

## Stage scopes

The service approver decides every request, so SAP cannot tell one app user from another: anyone holding
`creditnotes.approve` could decide any stage the service approver sits on. `CreditNoteApprovals:RoleStageScopes`
narrows a role to named stages. The list is filtered in SAP (`CurrentStage eq …`, so the count and the
cursor are of the scoped queue), and the detail, the attachment download, the decision and the add refuse
any other request with `403 CreditNoteApproval.OutsideStageScope`. A repeated add replays its first
answer after SAP has deleted the request, so the replay is checked against the stage that answer
recorded. Names rather than codes, because a stage's code differs between company databases.

The scope follows the **account's role in the database**, looked up by the caller's user id — never the
role claims on the request. The Web calls the API with its integration key and the user's token together,
so those claims include the key's `Admin`; reading them showed the wash bay the whole queue on the first
live run. A request with no user id (an integration key alone) sees every stage; an unknown or disabled
account is refused.

The wash bay (`WashBay`) is the role that must be scoped — `ApplicationRoles.RequiresCreditNoteStageScope` —
so a missing entry refuses its page instead of opening the whole queue. It decides and, since 2026-11-06,
adds the requests at its stage. For its rows to offer Approve at all, SAP must also list the service
approver on that stage.

Production, read back 2026-11-06: the one active wash bay template is #89 "Washbay Credit Notes"
(A/R credit memos), one stage, 44 **`Factory Washbay`**, one approval required of Opsassist, PdnAdmin,
Finacc, Software and Washbay — so the service approver is listed. Two older wash bay stages serve
inactive templates: 40 `Washbay` (#85 "Credit Notes to Wash") and 33 `Production WashBay` (#74
"Factory Credit Notes", which does not list Software). Until that day the setting named
`Wash Bay Approvals`, which production never had, so the wash bay's page answered
`StageScopeUnresolved`. A name here is matched against SAP's exactly (case aside); re-read the stages
when a template changes.

## The routes

`/api/credit-note-approvals` — see API.md §50. The Web page is `/credit-notes/approvals` (Admin and
WashBay, both offered the add; the manager reads credit notes on `/credit-notes` and decides nothing since
2026-10-28), and its attachment viewer streams through the Web's own `/download/credit-note-approval/{code}/{lineNum}`
bearer proxy, the same way proof-of-delivery files do.

## Drafts already credited some other way

A held draft can be credited twice without anyone noticing. One user raises the credit memo, SAP holds it
as a draft for approval, and another user keys the same credit straight into SAP. Nothing in SAP links the
two, so the draft stays Pending (or Approved) and SAP will still let it be approved and added. On production
on 2026-11-11, **107 of the 257 drafts SAP still held** were like this, behind 118 approval requests: 52 of
the 115 pending, and the rest approved but never added. In the one that was reported, the credit was keyed
again twenty minutes after the draft was raised.

A draft counts as already credited when a credit note has:

- the same customer (`CardCode`);
- the same Customer Ref. No. (`NumAtCard`, trimmed, case ignored);
- exactly the same `DocTotal`;
- an entry date (`CreationDate`) no earlier than the draft's own;
- `CancelStatus = csNo`. A cancelled credit note, and the document that cancelled it, credit nobody.
  (`Cancelled` cannot tell them apart: a cancellation document reads `tNO` or leaves it out.)

A draft with no reference is never matched. A match on customer and total alone is not enough: crate
returns of a round sum to the same customer are routine.

What a match does:

- **The open queues hide it.** "Awaiting decision" and "Approved, not added" leave it out, as SAP leaves a
  draft out once it is added, and their counts and paging allow for it. "All requests" still shows it, with
  an "Already credited · CN n" pill, so it can be found and cleared in SAP.
- **The drawer**, opened from "All requests", starts with the credit note's number, the day it was entered
  and by whom. Approve and Add are gone; Reject stays, because rejecting is how such a draft leaves SAP's
  queue. A draft the app cannot decide says to remove it in SAP.
- **Approve and add** run the check again, fresh, and refuse with `CreditNoteApproval.AlreadyCredited`. If
  SAP cannot answer, they refuse with `AlreadyCreditedUnchecked` rather than guess.

How the hiding works: `CreditNoteAlreadyCreditedIndex` reads the drafts SAP still holds (`Drafts`, credit
memos, `bost_Open`, `dasPending` or `dasApproved` — a few hundred, against the 6,700-odd approved requests
whose drafts were added long ago), checks them in one batched `CreditNotes` read, and reads the approval
requests of the ones it finds. It is cached for ten minutes and cleared whenever the app records a decision
or adds a credit note. The list still reads its rows from SAP on every load and asks for enough extra rows
to fill the page once the hidden ones are dropped; the list total and the queue tiles subtract the hidden
rows that fall in the same statuses, stages and filters. A failed build hides nothing; the write checks
still hold. A build measured 0.4–10 s on production, depending on how warm SAP was.

The `CreditNotes` read is batched by length because **the Service Layer refuses a query string over 2,048
characters** (error 207, counted before percent-encoding). A page of 24 pairs with a date bound in every
clause went over on production and the whole check failed. Pairs now share one date bound per read, and the
caller compares each note against its own draft's day.

## What happens on the add

0. **Quantities, if the person changed any.** The page offers each line's quantity as a box once the
   request is approved and the person may add it, so the wash bay can enter what it actually counted.
   The new quantities go up or down but stay above 0; a line is taken off in SAP, not here. Just before
   the add they are written to the draft with `PATCH Drafts(n)`, which is then read back: if a quantity
   did not stick, or the draft is no longer open and approved, nothing is added. A line holding one batch
   moves that batch by the same amount (in the inventory unit); a line split across batches or carrying
   serial numbers is refused. If SAP then refuses the add itself, the draft keeps the new quantities.

1. `DraftsService_SaveDraftToDocument` on a token the caller cannot cancel. A transport failure after it
   is answered by reading the request back: generated means it landed.
2. The new credit note's DocEntry comes from SAP's answer when it names the document, else from the
   request's `ObjectEntry`. If neither names it the add still succeeds, unresolved, and the projection
   sync job surfaces the document within minutes.
3. The credit note is read back and written through to the local projection, so the Credit Notes list
   shows it at once.
4. It is fiscalised through `IFiscalizationService.FiscalizeCreditNoteAsync` and the fiscal transaction is
   recorded (`DocumentType = CreditNote`, `SourceSystem = CreditNoteApprovalAdd`) — that row is what makes
   the Credit Notes list say "Fiscalised". A failure is an Exception Center incident
   (`Source = credit-note-fiscalization`); the add stands.

One add per approval request: a repeat replays the first answer, and SAP itself refuses a second
conversion of the same draft.

## What the live run settled

Run against `KEFALOS_TEST_3` on 2026-09-02, reads first and then the writes. Everything below was
measured, not inferred; several answers changed the design.

| Question | Answer |
|---|---|
| Does SAP accept `ObjectType eq '14'` and `Status eq 'arsPending'` as written? | **Yes.** 9,832 credit memo approval requests, 250 of them pending. |
| Does the `Drafts` set accept every field in `DraftSelect`? | **Yes**, by key and through a filtered collection read, headers and `DocumentLines`. |
| Does `Attachments2(n)/$value` stream a file attached in the B1 client? | **No — a SAP server fault, not a URL one.** |
| Is `ApproverPassword` mandatory on the decision PATCH? | **Only when an approver is named.** Naming nobody decides as the session user with no credential at all. |
| How long is `ApprovalRequestDecision.Remarks`? | **200 characters.** 201 is refused outright, and SAP rejects the whole decision rather than truncating. |
| What does `DraftsService_SaveDraftToDocument` answer? | **204 No Content.** It names the document it created nowhere. |
| Can an approved draft's quantities change before the add? (2026-11-09, draft 79079) | **Yes.** `PATCH Drafts(n)` with `DocumentLines[{LineNum, Quantity}]` answers 204, the draft stays `dasApproved` and its request `arsApproved`, and the credit note added from it (84478) carried the new quantity, 24 for 26. |
| How does that PATCH treat a batch the line already holds? | **It adds to it.** Sending 24 to a batch holding 26 left 50, with or without `B1S-ReplaceCollectionsFieldsOnPatch`; the add then fails `-4014`. Send the difference (−2). Batches read back only on a by-key `Drafts(n)` read with no `$select`. |
| Does `ApprovalRequests(code).ObjectEntry` name the credit note afterwards? | **No.** It is never populated, and a successful add **deletes the approval request** — reading it back afterwards returns 404. |

### The decision: name nobody, send nothing

Three payloads, three answers:

| Payload | Result |
|---|---|
| `ApproverUserName` + `ApproverPassword` | `204` — recorded |
| `ApproverUserName`, no password | `400 User code or password is incorrect [Message 131-93]`, request untouched |
| Neither | `204` — recorded against the **session user** |

So the default configuration sends no credential over the wire: `SAPSettings.ResolveNamedApprovalApprover()`
returns null unless `SAP:ApprovalApproverUsername` names somebody other than the login account. A
dedicated approver without a password is refused here, before the request is spent, because SAP's own
answer reads as a wrong password rather than as missing configuration.

A rejected decision is atomic: the request stays `arsPending` and can be decided again.

### A decision can approve the request and leave the draft Pending

Production, 2026-09-11: request **86300** (template "Credit Notes to Wash", one stage "Washbay", one
approval required) was approved from the app as SAP user `Software`. The request went `arsApproved`;
draft **78069** stayed `bost_Open` / `dasPending` with its update stamp untouched, so the B1 client
showed it "[Pending]" and the add refused it.

It was the first decision the app had ever made in production. Every B1-client decision among the
newest 2,000 credit memo requests moved its draft, non-superusers included, and so did the Service Layer
decisions in `KEFALOS_TEST_3` — all made by `manager`, a superuser. `Software` is not one. That is the
leading explanation and it is **not yet proven**; reproducing it means deciding a test request as a
non-superuser (`Mgmt` is listed on stage 21 "Jay Phil Tat").

What the code does about it: after an approval the decision handler reads the draft back and does not
say "can now be added" unless the draft is `dasApproved`; the list and detail say SAP left the draft
Pending. A draft stranded this way has to be raised again in SAP.

### The add: what survives it, and how the credit note is found

A successful conversion **deletes the approval request**. The draft survives and is the only witness:

| | before | after |
|---|---|---|
| `ApprovalRequests(code)` | `arsApproved` | **404** |
| `Drafts(n).DocumentStatus` | `bost_Open` | `bost_Close` |
| `Drafts(n).AuthorizationStatus` | `dasApproved` | `dasWithout` |

Two consequences, both now in the code:

- **The recovery signal after a failed call is the draft, not the request.** Asking the request whether
  the add landed always answers "gone", which would report every successful add as uncertain.
- **The created credit note is identified as a document the customer did not have before the add,
  carrying the draft's total.** Do *not* match on `DocNum`: drafts and credit notes number from
  different series, and here the credit note carrying a converted draft's `DocNum` belonged to an
  entirely different customer.

SAP refuses a conversion that would break a document rule, with its own words — a draft of
batch-managed items with no batch allocation came back as
`Cannot add row without complete selection of batch/serial numbers`, and the draft was left alone.

An end-to-end run on request 68381 approved, replayed the decision from its idempotency key without a
second PATCH, and added draft 73918 as credit note **DocEntry 84473 / DocNum 71485**, which the
projection then carried. Fiscalisation was attempted and refused locally (no API key configured), and
the failure was recorded as an Exception Center incident while the add stood — the intended behaviour.

### The attachment stream does not work in this landscape

Every form of the call — filename quoted, unquoted, extension-less, or omitted — answers:

```
404  {"error":{"code":404,"message":{"lang":"en-us",
      "value":"Fail to get the LINUX mount point for AttachmentsFolderPath"}}}
```

The Service Layer runs on Linux and its `AttachmentsFolderPath` is not mounted, so it can serve no
attachment for any document. Only a SAP administrator can fix that. Confirmed on both companies —
`KEFALOS_TEST_3` (attachment 28995, `epic 11.pdf`) and, on 2026-09-02, `KEFALOS_USD_NEW2` for the
document a manager actually clicked: approval request 84752 → draft 76744 → attachment 105063,
`brian 01_09_26.pdf`. The metadata reads back perfectly in both; it is only the bytes that are gone.

`appsettings.json` therefore ships `CreditNoteApprovals:AttachmentReadMode = Share`, which reads from
`SAP:AttachmentsPath`. Put it back to `ServiceLayer` once SAP mounts the folder.

**`SAP:AttachmentsPath` was pointing at a host that does not exist.** It read
`\\kfdb\b1_shf\Paths\Attachments\`; the file server is **`kfldb`**. Three things settled that on
2026-09-03:

- SAP's own `CompanyService_GetAdminInfo` reports its sibling folders as
  `ExcelFolderPath = \\kfldb\B1_SHF\Paths\Excel\` and `XMLFileFolderPath = \\Kfldb\b1_shf\Paths\Xml\` —
  same share, same `Paths\` parent.
- `kfldb` resolves to `10.10.10.6` and answers a ping. `kfdb` does not resolve at all.
- `\\kfldb\b1_shf\Paths\Attachments\` lists 99,144 files and holds both documents in question —
  `epic 11.pdf` and the production `brian 01_09_26.pdf`.

Nothing had ever exercised the value, which is how a dead host name survived: nothing calls
`UploadAttachmentToSAPAsync` or `AppendAttachmentToSAPAsync`, and POD uploads go to
`FileStorage:UploadPath`, a different location.

**That fix only reached `appsettings.json`, so production kept the dead host until 2026-11-05.** The
API's `web.config` also sets `SAP__AttachmentsPath`, still to `\\kfdb\`, and `Update-Production.ps1`
copies that variable from the package onto the server on every deploy, where it overrides
`appsettings.json`. `ShippedAttachmentsPathTests` now fails if the two files name different folders.

The refusal is reported in SAP's own words as `CreditNoteApproval.AttachmentUnavailable`, deliberately
**not** `AttachmentNotFound`: the drawer is listing the file by name, and telling somebody it does not
exist would send them hunting for a document that is right there. A share read separates the two
failures it can have, because different people fix them: a folder the server cannot open at all is an
`IOException` naming the path, and only a folder that opens but holds no such file is "not there".

That sentence reaches the person who clicked. The API answers `application/problem+json`, the Web's
download proxy forwards that body rather than a bare status code, `app.js` lifts its `detail` into the
error it throws, and the page shows it on the snackbar and in the drawer under the file list. What a
manager used to get for all of this was "The attachment could not be opened."

Driven end to end on 2026-09-03 — request 72419, `epic 11.pdf`, clicking View in the drawer. Against
the dead `kfdb` the page said, on the snackbar and under the file, with no console errors:

```
The attachment could not be read from SAP: The SAP attachments folder
'\\kfdb\b1_shf\Paths\Attachments\' could not be reached from this server.
```

With `kfldb` the same click opens the file: the viewer loads a `blob:` iframe, and the bytes are the
share's own. `GET /download/credit-note-approval/72419/1` answers 200 `application/pdf`, 1,168,414
bytes, `sha256 ec19b3da…` — identical through the Web proxy, straight off the API, read directly from
`\\kfldb\b1_shf\Paths\Attachments\epic 11.pdf`, and as saved to disk by clicking Download.

One thing to know when reading a failed download on a Windows desktop: **Internet Download Manager
takes the response before the page sees it.** With IDM's Advanced Integration on, the fetch answers
`204 Intercepted by the IDM Advanced Integration` with no body, IDM saves the real file into its own
category folder (`Downloads\Documents\`), and the page's own `<a download>` writes a 0-byte stub in
`Downloads\`. The file is fine; the empty one beside it is IDM's. Compare hashes before concluding
that a download is broken.

Note also that `Attachments2_Line.SourcePath` is where the person picked the file from — one row read
`C:\Users\Alice.Manyangala\Documents` — so it is not a location any server can open. The share read
uses the configured folder plus `FileName.FileExtension`.

### Signing in to the share

The share asks for a login. The API signs in with
`SAP:AttachmentsUsername` / `SAP:AttachmentsPassword` (`SAP:AttachmentsDomain` is prefixed to a bare
user name). Set them on the API server with `scripts/Set-SapAttachmentsShareLogin.ps1`, never in the
repository, which is public. It prompts for the account, signs in with the same Windows call the API
makes, reads a file from the folder (`-TestWrite` also writes and deletes one), and only then writes
the values into the live and slot `web.config` files. `-TestOnly` checks an account without changing
anything. Deployments carry the values forward from the live site.

The sign-in is shared by the whole process (`NetworkShareConnections`): the first reader signs in, and
the last one out disconnects. Windows does not count connections — two sign-ins to one share leave one
entry, and one cancel removes it for both — so readers that each signed in and force-cancelled could
cut each other off mid-read.

A refused sign-in reaches the snackbar the same way as a folder the server cannot open, and names
its reason: wrong password, locked out, or share not found.

`scripts/Get-SapAttachmentsStatus.ps1` reads all of this back on the API server without changing
anything: per API site, which one holds the public port, each attachment setting the API will use and
the file it came from, whether the share password is set (never the password itself), and the
attachment warnings and errors in that site's own log.

### Adding a draft with an attachment fails in SAP

The share fixes reading. It cannot fix the add: `DraftsService_SaveDraftToDocument` runs entirely
inside SAP, which copies the attachment onto the new credit note through the same unmounted folder.
Production, 2026-11-05 15:33 CAT, draft 86380:

```
400 Attachments folder not defined, or Attachments folder has been changed or removed [Message 131-102]
```

Nothing is added. Until a SAP administrator gives the Service Layer that folder, a draft with an
attachment can only be added in the SAP Business One client, and printing it there is what fiscalises
it. The API recognises the refusal by its message number (`CreditNoteDraftAttachments.IsAttachmentsFolderRefusal`)
and answers `CreditNoteApproval.AddBlockedByAttachmentsFolder`, which says exactly that. SAP's own words
stay in the log and the audit entry.

The page keeps the reason in the drawer after it re-reads the request, and closes the confirm dialog
once SAP has answered. Before, the reason lived only on a snackbar that faded in about five seconds,
leaving the dialog open and offering the same Add again.

### The service approver is on one stage of five

`manager` is listed as an approver on `Jay Phil Tat` only. Of 100 sampled pending requests, 31 can be
decided from this app today; the other 69 report, per row:

```
SAP stage 'BYO' does not list manager as an approver.
```

The other four stages carrying credit memos are `BYO`, `CMFS`, `Bron, Vic, Phil` and `Fiona Approvals`.
A SAP administrator adding the service approver to those opens the rest; nothing changes here.

### Performance

A page of 25 costs ~95 ms once the reference caches are warm and ~4 s cold (SAP login plus the first
read of each distinct user, template and stage, held for ten minutes). The first call after an API
restart can be far slower — 15 s was observed — while the startup sync jobs hold all six SAP
concurrency slots.
