# Inventory transfers

Depot-to-depot stock movement with an approval workflow, posted to SAP. The
highest-traffic page in the app by edit count.

## Sub-features

- Transfer list with status filtering
- Create a transfer (`/inventory-transfer/create`)
- Create a transfer **request** (`/transfer-request/create`), a distinct
  document from a transfer
- Approval and rejection, which drives the SAP posting queue

## How to get to it (user POV)

Sign in, open Inventory Transfers from the nav. The list is role-scoped: a depot
controller sees transfers and local stock only, so verifying as `admin` does not
prove what a controller sees.

## Driving it with cdp.py

```python
c.goto("http://localhost:5051/inventory-transfers")
c.wait_for("table, .empty-state")
rows = c.eval("document.querySelectorAll('tbody tr').length")
c.set_theme(True)
c.screenshot("transfers.dark.png")
```

**Proof it worked:** the table rendered with the expected row count, or an
explicit empty state, not a spinner. Assert on `c.text()` containing a known
document number rather than on the screenshot alone.

## Gotchas

- Listing pending transfers is a read that writes: enrichment opens an approval
  request per row. Do not loop this page to check whether it loads.
- Approval posts to SAP through the 10-second `inventory-transfer-posting` job.
  On a clean local database nothing is pending and nothing posts. Confirm before
  driving approval.
- Transfers and transfer *requests* are separate documents that have been
  redesigned against each other more than once. Check which one the change
  targets before writing the proof.
