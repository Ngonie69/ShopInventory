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
| `CreditNoteApprovals:AttachmentReadMode` | `Share` in `appsettings.json`; `ServiceLayer` if the key is absent | `Share` reads the file off `SAP:AttachmentsPath` with the share credentials instead of streaming `$value`, for a Service Layer that cannot serve the folder — which is this landscape, see below |

## The routes

`/api/credit-note-approvals` — see API.md §50. The Web page is `/credit-notes/approvals` (Admin and
Manager), and its attachment viewer streams through the Web's own `/download/credit-note-approval/{code}/{lineNum}`
bearer proxy, the same way proof-of-delivery files do.

## What happens on the add

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

**`SAP:AttachmentsPath` is unproven configuration.** Nothing calls `UploadAttachmentToSAPAsync` or
`AppendAttachmentToSAPAsync`, so this app has never written a file to that share — POD uploads go to
`FileStorage:UploadPath`, which is a different location. Two things to check on the API host before
trusting a share read:

- The value is `\\kfdb\b1_shf\Paths\Attachments\`, but SAP's own `CompanyService_GetAdminInfo` reports
  its sibling folders on a **differently spelled host**: `ExcelFolderPath = \\kfldb\B1_SHF\Paths\Excel\`
  and `XMLFileFolderPath = \\Kfldb\b1_shf\Paths\Xml\`. Same share, same `Paths\` parent, `kfldb` not
  `kfdb`. Confirm which name the file server answers to.
- The app pool identity needs read access to it, or `SAP:AttachmentsUsername` / `AttachmentsPassword`
  must be set.

A wrong path is not silent: the read names it, as below.

The refusal is reported in SAP's own words as `CreditNoteApproval.AttachmentUnavailable`, deliberately
**not** `AttachmentNotFound`: the drawer is listing the file by name, and telling somebody it does not
exist would send them hunting for a document that is right there. A share read separates the two
failures it can have, because different people fix them: a folder the server cannot open at all is an
`IOException` naming the path, and only a folder that opens but holds no such file is "not there".

That sentence reaches the person who clicked. The API answers `application/problem+json`, the Web's
download proxy forwards that body rather than a bare status code, `app.js` lifts its `detail` into the
error it throws, and the page shows it on the snackbar and in the drawer under the file list. What a
manager used to get for all of this was "The attachment could not be opened."

Driven end to end on 2026-09-03 — request 72419, `epic 11.pdf`, clicking View in the drawer:

```
The attachment could not be read from SAP: The SAP attachments folder
'\\kfdb\b1_shf\Paths\Attachments\' could not be reached from this server.
```

on the snackbar and in the drawer, with no console errors. That is the developer machine's own answer
(it cannot reach the share); the same road on the API host is what decides whether the path is right.

Note also that `Attachments2_Line.SourcePath` is where the person picked the file from — one row read
`C:\Users\Alice.Manyangala\Documents` — so it is not a location any server can open. The share read
uses the configured folder plus `FileName.FileExtension`.

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
