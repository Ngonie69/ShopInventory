# Customer portal

A separate, customer-facing surface with its own login, JWT and theme, served by
the same Web app under `/customer-portal/*`.

## Sub-features

- Customer login (`/customer-portal/login`), register, forgot/reset password
- Dashboard, invoices, credit notes, statements, payments, PODs, item summary
- Profile, activity log, support

## How to get to it (user POV)

Go to `/customer-portal/login` directly. This is **not** reachable from the
staff nav and does not share the staff session. Signing in as `admin` gives no
access here.

## Driving it with cdp.py

```python
c.goto("http://localhost:5051/customer-portal/login")
c.wait_for("form")
```

A staff smoke run does not cover this surface. Drive it with a customer account
in its own browser session.

## Credit notes (`/customer-portal/credit-notes`)

Proven end to end on 2026-11-06 with three scripts in `scripts/`, real Web against a stub API — no real
API, so no Quartz jobs and no SAP:

1. `seed_portal_user.py add C20417 <pwfile>` — the local Web DB ships **no portal accounts**. Login is
   checked in the Web's own `CustomerPortalUsers` (BCrypt, no API call), so one seeded row is enough. It
   hashes a random password with the app's own BCrypt.Net-Next and refuses a non-local host.
   `seed_portal_user.py remove C20417` deletes it and every `Customer*` row the sign-in wrote.
2. `portal_credit_notes_stub.py 5196 <pdf>` — answers `GET api/creditnote/customers` (eight notes) and
   `GET api/creditnote/{docEntry}/pdf` (404 unless `cardCode` is the seeded account); logs at `/__requests`.
   For `<pdf>`, run `CreditNotePdfTests` with `INVOICE_PDF_OUT` set and serve `credit-note-from-handler.pdf`.
3. Web with `ApiSettings__BaseUrl=http://localhost:5196/` on its own port, then
   `drive_portal_credit_notes.py` (env: `VERIFY_WEB_URL`, `STUB_URL`, `PORTAL_CARD_CODE`,
   `PORTAL_PASSWORD`, `SKILL_SCRIPTS`, `OUT_DIR`). 27 checks: nav link, one API read per load, figures,
   segments, Find, sheet, both downloads (it replaces `downloadPdfFromBase64` and reads `%PDF`), phone
   cards, and the sheet above the top bar.

Count stub requests **after** the page acts: a probe you curl by hand is in the same log. The portal
opens in its stored theme (dark here), so call `set_theme(False)` before a light shot.

## Gotchas

- Portal sheets (`.cpd-overlay`) must sit above the MudAppBar (z 1300). They were at 1200 until
  2026-11-06, and on a phone the bar hid the sheet's title and close button. Check with
  `document.elementFromPoint` at the close button's centre.

- Separate auth entirely: its own JWT, configured under `CustomerPortal:*`.
- The portal chrome still uses the `customer-*` class names. `.cpx` is a scope
  added around them, not a rename, so selectors built on a rename will not match.
- Dark-theme table rules here have lost specificity fights before. Verify portal
  tables in **both** themes, not just the one the change targeted.
- Statement figures are money. Assert the numbers.
