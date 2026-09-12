# Desktop fiscal credit notes (REVMax)

From **Desktop Sales**, open a fiscalised sale and choose **Credit notes**. The form reads the original
REVMax receipt, shows quantities remaining after saved credits, and accepts quantities and a reason.
**Create and fiscalise with REVMax** persists the credit before submitting it. A SAP DocEntry is not
required, and a later SAP invoice number does not replace the original fiscal reference.

The form uses the original receipt's prices, discounts and tax IDs/rates. The original device, global
receipt number and the fiscal day recorded when the sale was filed are retained. The REVMax lookup
envelope's FiscalDay is deliberately not substituted for a missing recorded day. A missing or
mismatched original reference stops preparation and requires reconciliation of that original receipt.

A receipt line is offered for credit when it carries a quantity, a positive value and a tax id. The
device's own `receiptLineNo` identifies the line when the receipt numbers them uniquely, and the
line's position does when it does not, so a receipt that repeats or omits those numbers is still
creditable. Receipts filed before 2026-09-12 do repeat them: the device stores a line number below 1
as 1, and invoices went out numbered from SAP's `LineNum`, which starts at 0 — receipt 216877
(GRC-FAC-20260911-286EEC7389FD) holds its three lines as 1, 1, 2. New receipts are numbered from 1. A line the device recorded as something other than a sale - FDMS's other line type is
`Discount` - or one with no quantity, no value or no tax id is listed as not offered, with its
reason, instead of stopping the credit; the credit total is still capped at the original receipt's
total, so leaving a line out cannot credit more than the receipt carried. Preparation is refused
only when no line can be credited, and the refusal names every line and its reason.

Each credit has a permanent `DCN-` number, an immutable payload and a unique request key. A retry with
different quantities under the same key is refused. Submitted or uncertain credits reserve their
quantities and amount. **Check fiscal status** only reads REVMax; it never resubmits. A note saved before
submission can be continued with **Continue fiscalisation**. An uncertain submission that is not found
remains reserved for investigation rather than being treated as permission to file again.

The saved result distinguishes fiscalisation from back-office processing. These desktop fiscal
credits do **not** automatically post a SAP credit memo, refund cash or restock inventory. They show
**Pending credit posting** until back-office reconciliation is performed. The existing sale continues
through its original SAP posting/consolidation flow. Review the saved credit and its original receipt
when recording that adjustment; do not fiscalise the same credit again under a SAP number.

## Deployment

Deploy ShopInventory API and Web together, including migration `20260911132801_AddDesktopCreditNotes`.
The migration creates a separate durable credit table and prevents deletion of a referenced sale.
No change or deployment to the dormant Fiscalisation repository is needed. REVMax must be enabled
and selected as the fiscal provider. The new endpoints use the existing authenticated API access and
warehouse scope checks.

Migration metadata can be generated or checked without loading operational credentials by passing
`-- --metadata-only` to the EF tool. This mode is for metadata commands, not database updates.

Tests use SQLite and stubbed REVMax responses. No live fiscal receipt is issued during verification.
