# Retail Sales Orders (was Mobile Orders) and Van Sales Orders

Sales orders captured on the field app, reviewed and approved here before they post to SAP. One
page, two routes: `/van-sales-orders` shows the van-sales orders, `/mobile-drafts` everything else.

## Sub-features

- Tiles: all-time total, draft, pending (with oldest waiting), approved
- Status tabs (only statuses some order is in; there is no Delivered tab)
- Filter panel: order #, customer, order date, delivery date, currency, total, SAP doc #
- Column sorts, rows-per-page, Previous / Next
- "Load older orders": the default window is the last 90 days plus every open order
- Row actions: view drawer, approve, cancel, back to draft, delete, convert to invoice, POD
- Selection across pages, bulk approve, export to Excel (selection, or every matching order)
- Delivered / Invoiced labels, read from SAP for the rows on screen
- Route checklist (`?view=checklist`, retail only): pick a day and order/delivery date; each Delivery
  Routes route with its shops ticked where an order came in; shops on no route listed apart; Export
  to Excel (Checklist / Route Summary / Order Lines / Not Ordered sheets)

## How to get to it (user POV)

Sign in as Admin, Cashier, Merchandiser or SalesRep, then Sales → Retail orders (URL still
`/mobile-drafts`). The page heading reads Retail Sales Orders.

## Driving it with cdp.py

`scripts/drive_mobile_orders_paging.py` drives tabs, a typed filter, a sort both ways, Next, export
and Load older orders, and writes `result.json` with one entry per check.

The page needs mobile orders, and the local database has few. Run the API against a throwaway
database instead of seeding the dev one. In Development a fresh database is seeded with
`admin` / `admin123` and two-factor off, so login needs no change to the dev admin:

```powershell
$env:ConnectionStrings__DefaultConnection = '<dev connection string, Database=mobile_paging_e2e>'
$env:ASPNETCORE_ENVIRONMENT = 'Development'; $env:SAP__Enabled = 'false'
dotnet run --project ShopInventory/ShopInventory.csproj --no-launch-profile --urls http://localhost:5116
```

Start the Web with `ApiSettings__BaseUrl=http://localhost:5116/`, seed `SalesOrders` rows with
`Source = Mobile` (supply `RowVersion`; it is store-generated), run the driver with
`VERIFY_WEB_URL`, then drop the database.

**Proof it worked:** the footer range (`.mox-foot-range`, "Showing 1–50 of N orders") and the
tiles match a SQL count over the same window, every row under a tab reads that status, and the
Web log shows the list read with `pageSize=<rows per page>`. Only an export reads up to 10,000.

## Route checklist proof (2026-10-28)

Seed orders on route stops (SPA070 and `SPA050 USD` are one shop, Spar Bridge, on BORROWDALE; TMP114
is on no route) and order lines in `SalesOrderLines`. Check a shop ticks once for both codes, a
cancelled order ticks nothing, delivery-date mode moves the ticks, and the export follows the day
chip. The page downloads through `window.downloadFile(name, base64)`: wrap it in the page to keep
the bytes, then open the file in Excel over COM. The tab groups share labels ("All" is both a day
chip and a shop filter): select tabs by their group's `aria-label`.

## Gotchas

- **Do not run the Web on 5061.** It is SIP-TLS, on Chrome's unsafe-port list, so headless Chrome
  lands on `chrome-error://chromewebdata/` while curl gets a 200. Use 5071.
- The default page size comes from the app's settings (50 locally), not the 10 in the code.
- Blazor drops `aria-expanded` when it is false; find the Filters button by its text.
- Column headers are upper-cased by CSS and `innerText` reports them that way; match `textContent`.
- A filter narrows within the 90-day window, as it did before paging: an old posted order only
  turns up after Load older orders.
