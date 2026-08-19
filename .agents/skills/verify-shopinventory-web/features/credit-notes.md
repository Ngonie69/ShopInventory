# Credit notes

Customer credits raised against invoices, posted to SAP and fiscalised.

## Sub-features

- Credit note list with filters
- Create (`/credit-notes/create`)
- Reason / Comments field, blank on older projected records

## How to get to it (user POV)

Sign in, open Credit Notes from the nav, or go straight to `/credit-notes`.

## Driving it with cdp.py

```python
c.goto("http://localhost:5051/credit-notes")
c.wait_for("table, .empty-state")
assert "Credit" in c.text("h1, .page-title")
```

**Proof it worked:** the list renders and a known credit note number appears in
`c.text()`. For a create-path change, drive the form and confirm the new number
appears in the list afterwards. The list is the observable end state, not the
success toast.

## Gotchas

- Document lists answer headers-only. A line-level assertion needs the detail
  page, not the list.
- A blank Reason on an old credit note is expected historical data, not a bug
  your change introduced.
- Money-touching. Anything altering totals, tax base or currency needs the
  numbers asserted, not eyeballed from a screenshot.
