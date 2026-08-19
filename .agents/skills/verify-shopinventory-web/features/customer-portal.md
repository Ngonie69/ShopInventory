# Customer portal

A separate, customer-facing surface with its own login, JWT and theme, served by
the same Web app under `/customer-portal/*`.

## Sub-features

- Customer login (`/customer-portal/login`), register, forgot/reset password
- Dashboard, invoices, statements, payments, PODs, item summary
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

## Gotchas

- Separate auth entirely: its own JWT, configured under `CustomerPortal:*`.
- The portal chrome still uses the `customer-*` class names. `.cpx` is a scope
  added around them, not a rename, so selectors built on a rename will not match.
- Dark-theme table rules here have lost specificity fights before. Verify portal
  tables in **both** themes, not just the one the change targeted.
- Statement figures are money. Assert the numbers.
