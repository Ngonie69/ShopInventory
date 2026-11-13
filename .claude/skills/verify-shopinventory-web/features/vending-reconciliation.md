# Vending reconciliation (cash-ups)

A vending cashier's end-of-day count held against their sales, and its approval.
One cash-up per cashier (a `CartVendor` account), per depot warehouse, per day.
Money is per currency (USD, ZiG) and never added across currencies.

## Sub-features

- The week, month or today as rows of cashier-days: invoiced, counted (less the
  float, plus non-cash slips), banked, variance and status.
- **Not cashed up**: a day with sales and no count shows "Not counted" and a dash,
  never a variance. This is the rule the view was deferred for — check it first.
- Open cash-up (Admin, Cashier): opening float, the count as a total or by notes
  and coins, banked, deposit reference, non-cash tenders against the sales,
  reason, note; the variance is previewed as it is typed.
- Review (Admin): Approve or Query from the same dialog. Approve is refused, with
  the reason shown, to anyone who ever saved the count, while sales have changed
  since it was saved, and while a variance has no reason.
- Manager: reads only — no Open cash-up, the dialog is disabled.
- Variance report: an Excel workbook, one row per cashier-day per currency, totals
  per currency, uncounted cells blank.

## How to get to it (user POV)

Sidebar **Tills & Vending → Reconciliation**, or the **Reconciliation** button in
the Vending sales header. `/vending/reconciliation`.

## Driving it

`scripts/drive_vending_reconciliation.py` drives it as a supervisor (Cashier), an
Admin and a Manager and checks 19 observable states. It needs data: vending
cashiers, a week of `KefalosVending` sales and some counts. The local dev
database has none, so use the scratch-database recipe (API on 5116, Web on 5071)
and seed it — users by copying the `admin` row, sales by SQL, and the history of
counts **through the API** (`PUT api/vending/cash-ups`, then `/approve` and
`/query` as Admins) so the history went through the real handlers.

The script's day labels ("Thu 12 Nov", "Fri 13 Nov") and cashier names are that
seed's; reseed, or edit them, for another day.

Before starting the scratch API, point its outbound integrations at a closed port
(`SAP__ServiceLayerUrl`, `Revmax__BaseUrl`, `Cartrack__BaseUrl`,
`Fiscalisation__BaseUrl`, `TransferEventListener__BaseUrl`). `SAP__Enabled=false`
alone does **not** stop the startup daily-stock fetch from reading the Service
Layer, which is shared with production.

The dialog is `position: fixed`, so a full-page screenshot stops at the viewport;
the script shoots it at its top and again scrolled to its foot.

To prove the export, set `Browser.setDownloadBehavior` on the CDP session, click
**Variance report**, then open the file in Excel over COM with alerts on.

## What proves it

- The "Not cashed up" row has no variance figure (`—`), in the table and the
  workbook.
- A count keyed by the supervisor shows "Awaiting approval", "counted by
  supervisor"; after an Admin approves it, "approved by admin" and every input
  is disabled.
- An Admin who saved a count sees Approve disabled with "another Admin has to
  approve it".
- No console errors in any of the three sessions.
