# Invoices

SAP A/R invoices: list, detail drawer, PDF, credit notes, cancel, and fiscalising one or many.

## Sub-features

- Tiles: invoices, total value, customers, VAT — over every match, added up by SAP (`$apply`)
- Filters: doc number, customer code (both typed, applied after a pause), fiscal status, from / to dates
- Quick filter: exact doc number, or text in the customer code or name (any case), on the API
- Rows-per-page (up to 200), Previous / Next
- Select a row, a page, or "Select all not fiscalised" (every match, any page), then Fiscalise

## How to get to it (user POV)

Sign in as Admin, Cashier, StockController or Manager, then Sales → Invoices, or `/invoices`.

## Driving it with cdp.py

`scripts/drive_invoices_paging.py` checks the unfiltered page and tiles, Next, the quick search, a
customer with each fiscal state, and Select all across pages, against counts SAP gives directly
(`EXPECT` JSON). The fiscal filter is a NocturneSelect: click `#ivx-f-fiscal`, then the
`.nsel-open .nsel-item` by its text.

The list needs SAP: the API refuses it with `SAP:Enabled=false`. On a throwaway database run the API
with `SAP__Enabled=true` against the test company and `Fiscalisation__Enabled=false`; seed
`DesktopFiscalTransactions` (`DocumentType` "Invoice"; a `QRCode` makes it Fiscalised) for one
customer's latest DocNums to exercise the fiscal states.

**Proof it worked:** `.ivx-foot-range` ("1–50 of N invoices") and the tiles (`.ivx-stat-value`) match
SAP's own `$count` and `$apply` for the same filters, and every row under a fiscal filter reads that state.

## Gotchas

- SAP's `contains()` is case-sensitive and the Service Layer refuses `tolower()`/`toupper()`, so the
  search tries the text as typed, in capitals and in title case. "kefalos" alone matched 0 of 4,535.
- The fiscal filter and Select all read up to 5,000 matches from SAP and keep the scan for two minutes;
  a filter change reads again, a page turn does not. "Newest 5,000 matches checked" means narrow it.
- On 2026-10-07 KEFALOS_TEST_3 held 305,377 invoices (latest June 2026), 705 customers, 969 for SPA011.
