# Credit note approvals (SAP)

A/R credit memos raised in the SAP B1 client and held by SAP's own approval procedure: a manager
reads the draft and its attachment, approves or rejects it, and adds the approved one as the credit
note. SAP is the source of truth; nothing is mirrored locally.

## Sub-features

- Queue with a status filter (awaiting approval, approved not yet added, all)
- Drawer: draft header and lines, approver lines, current stage, attachments
- Attachment viewer (PDF in a blob `<iframe>`, images in an `<img>`) and download
- Approve / reject with remarks, recorded in SAP as the service approver
- Add credit note (money-touching: converts the draft, projects it, fiscalises it)

## How to get to it (user POV)

Sign in as an Admin or Manager, open Credit Note Approvals under Sales & Billing, or go straight to
`/credit-notes/approvals`. A Cashier has no link and is bounced by the role gate — verifying as
`admin` proves nothing about a Manager's nav; check that separately.

## Driving it with cdp.py

```python
c.goto("http://localhost:5051/credit-notes/approvals")
c.wait_for("table, .cna-empty, .cna-alert")
assert "Credit Note Approvals" in c.text("h1")
```

**Proof it worked:** the table rendered with a known request code in `c.text()`, or an explicit empty
state, or — with SAP unreachable — the page's own error alert rather than a spinner. After a decision
the row's status chip changes and the drawer's action panel switches to "Add credit note"; after an add
the new DocNum appears in the snackbar and on `/credit-notes`.

## Gotchas

- **Every decision and every add writes to a real SAP company.** Point `SAP:CompanyDB` at
  `KEFALOS_TEST_3` before driving either, and never at production.
- The list is read live from SAP: with the stored SAP credentials stale the page shows its error
  state. That is a valid screenshot of the failure path, not proof of the feature.
- Attachments are blob URLs made by `createAuthenticatedObjectUrl`; a screenshot of the open viewer is
  the proof, `c.text()` sees nothing inside the iframe.
- `backdrop-filter` renders flat under headless Chrome; do not read the sticky header's frosting as a
  bug from a shot alone.
- Type size in dark mode measures 16px in the harness; compare sizes in light.
