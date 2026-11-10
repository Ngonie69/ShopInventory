# SAP credit memo guard

Stops an A/R credit memo from being added in SAP B1, from any client, when ZIMRA would refuse it. The
main case is a credit memo worth more than is left on the invoice it credits.

Files: [`scripts/sap-credit-memo-guard/`](../../scripts/sap-credit-memo-guard/)

| File | What it does |
|---|---|
| `01-KEF_CREDIT_MEMO_CHECK.sql` | Creates the check as its own procedure. Read-only. Does nothing on its own. |
| `02-TransactionNotification-call.sql` | The block to paste into `SBO_SP_TransactionNotification`. This switches the guard on. |
| `03-Replay-KEF_CREDIT_MEMO_CHECK.sql` | Read-only. Lists the past credit memos the check would have refused. |
| `Test-CreditMemoGuard.ps1` | Adds two test drafts in a TEST company through the Service Layer: one must be refused, one accepted. Deletes them afterwards. |

## Why

The fiscal console refused two credit notes on 9 Nov 2026 with `RCPT035`: the credit
notes against an invoice may not add up to more than the invoice.

Both were keyed in the B1 client by copying a crate line from the invoice and then typing more crate
lines by hand:

| Credit note | Copied from the invoice | Typed by hand | Total | Left on the invoice |
|---|---|---|---|---|
| A | 22 × CRA002 from the invoice | 5 × CRA002 | 311.85 | 265.65 |
| B | 4 × CRA002 from the invoice | 3 + 3 × CRA002 | 115.50 | 46.20 (34.65 already credited) |

SAP stops a **copied** line going past the invoice's open quantity. A **typed** line isn't linked to
anything, and a credit memo with nothing copied is linked only by the number in Customer Ref. No. So
SAP lets the credit memo go past the invoice. Nothing in B1 blocks it.

Measured on `KEFALOS_USD_NEW2` on 10 Nov 2026, over the 2,813 credit memos dated from 1 Oct (not
cancelled), resolving each one's invoice the way the platform does (see below):

| Credit memos | Count | Guard |
|---|---|---|
| Copied from one invoice, within it (incl. 335 with typed crate lines) | 2,206 | allowed |
| Copied from one invoice, **over what was left on it** | **143** | refused, 70003 |
| Nothing copied, Customer Ref. No. is the customer's invoice, within it | 420 | allowed |
| Nothing copied, **over the invoice in Customer Ref. No.** | **9** | refused, 70003 |
| Nothing copied, Customer Ref. No. is **another customer's** invoice | 18 | refused, 70004 |
| Nothing copied, Customer Ref. No. **blank or not a number** | 16 | refused, 70004 |
| Nothing copied, Customer Ref. No. is **no invoice at all** | 1 | refused, 70004 |
| Copied from more than one invoice, or in another currency | 0 | refused, 70001 / 70002 |

That is 187 refusals in six weeks (6.6%), and every one is a credit note the fiscal platform would
refuse anyway. Of the 143 over-credits, most had typed crate lines. The rest had every line copied,
but earlier credit memos had already used up most of the invoice. Four SAP users raised
nearly all the copied over-credits, and two others most of the Customer Ref. No. problems.
Earlier credits dated before 1 Oct were not counted, so the refusals against older invoices are, if
anything, under-counted.

## What it refuses

It checks a credit memo when it is **added** (object 14) and a credit memo **draft** when it is added
or changed (object 112). The draft check is what stops the wash bay when they send a draft for approval,
not hours later when it is added.

### Which invoice a credit memo is against

The same rule the fiscal platform uses to file it (`SapCreditNoteLinkResolver` in the Fiscalisation
repo):

- **Any line copied from an A/R invoice:** that invoice. Customer Ref. No. is ignored. Credit note A
  names a different invoice there, but is filed against the one it was copied from.
- **Nothing copied:** Customer Ref. No. (`NumAtCard`) is the invoice's number. It must be exactly one
  non-cancelled A/R invoice of the same customer.

| Code | Refused when | Message the user sees in B1 |
|---|---|---|
| 70001 | lines are copied from more than one invoice | `KEF: this credit note copies from 2 invoices. ZIMRA takes one invoice per credit note: raise a separate credit note for each invoice.` |
| 70002 | the currency differs from the invoice's | `KEF: this credit note is in ZiG but invoice 100234 is in USD. A credit note must be in its invoice's currency.` |
| 70003 | the total is over what is left on the invoice | `KEF: 311.85 is more than is left on invoice 100234: 265.65 invoiced, 0.00 already credited, 265.65 left. Reduce the credit note.` |
| 70004 | nothing is copied, and Customer Ref. No. is not exactly one invoice of this customer | `KEF: nothing is copied from an invoice, and Customer Ref. No. 'CRATES' is not an invoice of C00123. Use Copy From, or put the invoice number in Customer Ref. No.` (or, when the customer has two invoices with that number: `… Create the credit note with Copy From on the invoice instead.`) |

"Already credited" is the total of every credit memo added before this one that the platform would file
against the same invoice, by either route, not counting cancelled ones. That is how the fiscal console
counts "previously credited". Amounts are in the invoice's currency and compared to the cent.

**Not refused:**
- **Typed lines next to copied ones.** They are allowed, and their amount counts toward the total.
  Crates can still be credited this way as long as the invoice has room.
- **Updates to a credit memo already added**, so remarks can still be edited on old ones.

When a user is refused, the fix is to lower the quantity, or split the credit across the invoices the
goods came from. A credit note copied from each invoice in turn is always accepted while that invoice
has room.

ShopInventory's approval add goes through the same procedure: adding a draft from
`/credit-note-approvals` is a credit memo add in SAP. `AddApprovedCreditNoteHandler` already passes
SAP's refusal text through as `SapRejected`, so the page should show the `KEF:` sentence. Not yet seen.
Check it on TEST_3 after step 4.

## Installing

Do this in `KEFALOS_TEST_3` first, then `KEFALOS_USD_NEW2`. It needs a HANA user that may create
procedures in the company schema (the B1 DB user or SYSTEM), using HANA Studio, DBeaver or `hdbsql`.

1. **Back up the current `SBO_SP_TransactionNotification`.** Save its definition to a file:
   ```sql
   SELECT DEFINITION FROM PROCEDURES
    WHERE SCHEMA_NAME = 'KEFALOS_TEST_3' AND PROCEDURE_NAME = 'SBO_SP_TransactionNotification';
   ```
2. `SET SCHEMA "KEFALOS_TEST_3";` then run `01-KEF_CREDIT_MEMO_CHECK.sql`.
3. Run `03-Replay-KEF_CREDIT_MEMO_CHECK.sql`. It only reads. On production, first fill in and run the two
   commented `CALL`s with the DocEntries of credit notes A and B: both must answer 70003 with the
   265.65 / 46.20 figures above, and the list should hold about 187 rows from 1 Oct
   (152 × 70003, 35 × 70004). A large gap from that means the SQL and the Service Layer replay disagree:
   stop and find out why before step 4.
   On TEST_3, leave the two `CALL`s out.
4. **Switch it on.** Open the procedure, paste the block from `02-TransactionNotification-call.sql`
   just before its final `select :error, :error_message FROM dummy;`, and re-create the procedure.
   Leave everything else in it unchanged.
5. **Prove it.** Run this from a machine that reaches the Service Layer:
   ```powershell
   .\scripts\sap-credit-memo-guard\Test-CreditMemoGuard.ps1 -ServiceLayerUrl https://10.10.10.6:50000/b1s/v1 -CompanyDB KEFALOS_TEST_3 -UserName manager
   ```
   It tries five drafts: three that must be refused (copied plus typed over the invoice, nothing copied
   but over the invoice named in Customer Ref. No., and nothing copied with a reference that is no
   invoice) and two that must go through. Before step 4 the three report `FAIL … HTTP 201`, because SAP
   accepts them. Seen on 10 Nov 2026: 1.56 and 1.15 drafts against a 0.41 invoice. After step 4 all
   five must report `PASS`. It deletes every draft it added, and refuses to run against a company
   without `TEST` in the name.
6. In the B1 client on TEST_3, copy a line from an invoice into a credit memo, type in an extra line
   worth more than the invoice, and press Add. B1 must show the `KEF:` message.
7. Repeat steps 1 to 4 on `KEFALOS_USD_NEW2`, then tell the wash bay and accounts users what the
   message means.

## Switching it off

Delete the block between `-- KEF credit memo guard` and `-- end KEF credit memo guard` from
`SBO_SP_TransactionNotification` and re-create it. Or restore the definition saved in step 1.
`KEF_CREDIT_MEMO_CHECK` can stay; nothing calls it once the block is gone.

## Not covered here

- The **187 past credit memos** the guard would have refused are already in SAP. Each one that reaches
  the fiscal console will be refused the same way. They need an accounts decision: cancel and re-key
  them within the invoice (or against the right customer's invoice), or write them off in the console.
  The replay script's list is the worklist.
- **ShopInventory's own fiscaliser disagrees with the platform.** `SapCreditNoteFiscaliser`, used by
  the credit-memo sweep and the approval add, refuses every credit memo with nothing copied ("not based
  on an invoice"), while the platform files those against the invoice in Customer Ref. No. So a
  B1-keyed claim of that kind is fiscalised only when it is printed through the platform's B1 add-on.
