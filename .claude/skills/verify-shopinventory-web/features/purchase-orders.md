# Purchase Orders

Orders raised to suppliers, from SAP or from the local table, with approval and goods receipt.

## Sub-features

- Data source toggle: SAP (default) or Local
- Figures: total, drafts, pending, approved (approved or part received), received — over every match
- Filters: supplier code (exact), status, ordered from / to; **Apply** to run them
- Rows-per-page, Previous / Next
- Row actions: view drawer (lines), edit, submit, approve, receive goods, GRPO from a SAP order

## How to get to it (user POV)

Sign in, Purchasing → Purchase Orders, or go to `/purchase-orders`.

## Driving it with cdp.py

`scripts/drive_credit_notes_purchase_orders_paging.py` records what the SAP source answers, then drives
the local source: one page, the figures, a status filter, Next. The status filter is a NocturneSelect:
click `.pdx-filters .nsel-btn`, then the `.nsel-open .nsel-item` by its text, then **Apply**.

**Proof it worked:** `.pdx-count` ("N orders · Local") and `.pdx-foot-info` ("Showing 1–50 of N")
match a SQL count, the figures (`.pdx-stat-num`) match counts by status, and every row under a status
filter (`.pdx-cell-status`) reads that status.

## Gotchas

- Both sources page where they are read; the SAP source asks SAP for `$count` and one `$top`/`$skip`
  page. SAP orders read only Approved, Received or Cancelled, so the SAP source's Drafts and Pending
  are always 0 and filtering SAP by Draft, Pending, On hold or Partially received shows nothing.
- `SAP__Enabled=false` does not stop the Service Layer client. On 2026-10-07 a local API reached SAP
  TEST_3 for this page: 3,228 orders, Approved 1,725 + Received 1,482 + Cancelled 21.
- The receive dialog uses the row's lines, so list rows still carry them; keep pages small.
