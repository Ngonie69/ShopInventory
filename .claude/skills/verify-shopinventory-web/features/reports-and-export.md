# Reports and Excel export

A family of reports under `/reports`, most offering an .xlsx export built by
`ReportExportService`.

## Sub-features

- Reports Hub (`/reports`, no `tab`): opens loaded for a period (`?period=today|yesterday|week|month|30d|custom&from=&to=`)
  with four headline tiles against the period before, a "Needs attention" list, daily sales, top
  customers/products, owed by age, payments by method, a role-filtered searchable directory of every
  report, and an Export to Excel workbook. Root class `.rhb`, sheet `reports-hub.css`.
- The full reports behind it (`/reports?tab=profit|sales|products|stock|payments|customers|creditnotes|purchasing|aging|alerts|slowmoving|fulfillment`),
  opened already loaded for the hub's dates, with a `.rpt-back-link` back to the hub
- Individual reports: `/reports/item-volume`, `/reports/order-fulfillment`,
  `/reports/customer-revenue`, `/reports/partner-performance`,
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

**The hub against a stub API.** It reads twelve `api/report/*` routes and nothing else, so a stub
that answers login, `auth/me` and those twelve drives it without SAP. The worked example is
`artifacts/verify/20261029-reports-hub/` (`stub_api.py`, `run_web.py`, `drive_hub.py`; gitignored):
fixtures whose days sum to the headline, `--slow-stock` to prove the page fills in before stock
answers, `--fail-aging` to prove a part SAP did not answer leaves a named gap rather than zeros.

## Gotchas

- **The Web caches every report read for 10 minutes** (`ReportService`), failures excepted. A
  second run against the same Web gets instant answers, so a "shows X while Y is still loading"
  check only means something on a freshly started Web.
- Wait for the hub to be *settled* (`.rhb-kpis` present, no `.rhb-kpi-skel`, no
  `.rhb-attn-pending`), not merely for the pending rows to be absent: before the first render
  there are none, and a check that runs then reads an empty page.
- The tab view is not built for a theme flipped under it: toggled with `set_theme` its tables
  render half-dark. Shoot it after setting `localStorage['app-theme']` and loading fresh.
- `.dark-theme table thead th` in app.css (0,1,3) paints header cells slate on any page table
  whose own rule is shallower — the hub's is `.rhb .rhb-table th`.

- The download itself cannot be proven from a screenshot. Verify a generated
  `.xlsx` by opening the package and checking parts, not by watching a click.
- Date filters are Nocturne date fields, not native `<input type="date">`. Drive
  them through the component's own input, and read
  `.claude/skills/nocturne-dropdowns-and-dates` before changing one.
- Report pages share CSS prefixes with each other. A styling fix verified on one
  report is not verified on the rest. The map is the scope.
