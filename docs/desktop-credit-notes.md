# Desktop fiscal credit notes (REVMax)

From **Desktop Sales**, open a fiscalised sale and choose **Credit notes**. The form reads the original
REVMax receipt, shows quantities remaining after saved credits, and accepts quantities and a reason.
**Create and fiscalise with REVMax** persists the credit before submitting it. A SAP DocEntry is not
required, and a later SAP invoice number does not replace the original fiscal reference.

The form uses the original receipt's prices, discounts and tax IDs/rates. The original device, global
receipt number and the fiscal day recorded when the sale was filed are retained. The REVMax lookup
envelope's FiscalDay is deliberately not substituted for a missing recorded day. A missing or
mismatched original reference stops preparation and requires reconciliation of that original receipt.

Each credit has a permanent `DCN-` number, an immutable payload and a unique request key. A retry with
different quantities under the same key is refused. Submitted or uncertain credits reserve their
quantities and amount. **Check fiscal status** only reads REVMax; it never resubmits. A note saved before
submission can be continued with **Continue fiscalisation**. An uncertain submission that is not found
remains reserved for investigation rather than being treated as permission to file again.

The saved result distinguishes fiscalisation from back-office processing, and reports both. A credit
does **not** refund cash — that stays a counter action — but the SAP credit memo and the stock return
now follow on their own.

## The SAP credit memo

Raised automatically, and only ever after ZIMRA has accepted the credit. When the sale is already in
SAP the memo appears within seconds; when it is not — the ordinary case at a till, where a sale posts
hours after it is rung up — the credit shows **follows once this sale posts**, and the posting pass
raises the memo the moment the invoice exists. A sweep behind that catches what the pass cannot: a
sale adopted rather than posted, a process that died between the two, and any memo SAP refused.

The memo is based on the invoice, so SAP takes the batches from the document being credited; it
credits only the lines and quantities the credit names, and carries the credit's `DCN-` number in
`NumAtCard` so a retry finds a memo whose reply was lost rather than raising a second.

Two cases are still a person's job, and say so rather than guessing. A sale that reached SAP inside an
end-of-day **consolidated invoice** has no invoice of its own to credit — a standalone memo would have
to choose batches with nothing to take them from — and a credited receipt line that cannot be tied
back to an invoice line is refused outright, because a memo built on a guessed line credits the wrong
item and moves the wrong stock. Both show **raise by hand against the consolidated invoice** with the
reason beside them. The fiscal credit is filed either way; it is the half nothing else can do.

The credited units go back on the shared stock ledger as soon as ZIMRA accepts the credit, not when
SAP does — they never left the ledger through SAP, since the sale deducted them locally when it was
rung up, and holding them until the evening would have a shop refusing sales for goods on its own
counter. They are returned once, tracked on the credit's own row.

Once the memo exists it reads as fiscalised wherever credit notes are listed, because the receipt was
filed under the `DCN-` number rather than the SAP one. Do not fiscalise it again under its SAP number.

## Deployment

Deploy ShopInventory API and Web together, including migrations `20260911132801_AddDesktopCreditNotes`
and `20260912020300_AddDesktopCreditNoteSapPosting`. The second adds the back-office columns and
backfills every existing credit as `Deferred`, so credits saved before it are picked up and posted
rather than stranded.
The migration creates a separate durable credit table and prevents deletion of a referenced sale.
No change or deployment to the dormant Fiscalisation repository is needed. REVMax must be enabled
and selected as the fiscal provider. The new endpoints use the existing authenticated API access and
warehouse scope checks.

Migration metadata can be generated or checked without loading operational credentials by passing
`-- --metadata-only` to the EF tool. This mode is for metadata commands, not database updates.

Tests use SQLite and stubbed REVMax responses. No live fiscal receipt is issued during verification.
