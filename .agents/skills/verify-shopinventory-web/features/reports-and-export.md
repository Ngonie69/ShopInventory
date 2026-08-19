# Reports and Excel export

A family of reports under `/reports`, most offering an .xlsx export built by
`ReportExportService`.

## Sub-features

- Report index (`/reports`)
- Individual reports: `/reports/item-volume`, `/reports/order-fulfillment`,
  `/reports/customer-revenue`, `/reports/account-sales-payments`,
  `/reports/volume-conversions`, `/reports/merchandiser-purchase-orders`
- Date-range and filter controls on each
- Excel download

## How to get to it (user POV)

Sign in, open Reports, pick a report, set a range, view or export.

## Driving it with cdp.py

```python
c.goto("http://localhost:5051/reports/item-volume")
c.wait_for(".ivx-page, table")
c.screenshot("item-volume.light.png")
```

**Proof it worked:** the report renders rows for the chosen range. A page that
loaded is not proof for a report. Assert a figure you can independently check.

## Gotchas

- The download itself cannot be proven from a screenshot. Verify a generated
  `.xlsx` by opening the package and checking parts, not by watching a click.
- Date filters are Nocturne date fields, not native `<input type="date">`. Drive
  them through the component's own input, and read
  `.claude/skills/nocturne-dropdowns-and-dates` before changing one.
- Report pages share CSS prefixes with each other. A styling fix verified on one
  report is not verified on the rest. The map is the scope.
