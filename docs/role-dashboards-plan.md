# Role dashboards — plan

Status: **all three phases built.** Every role now lands on a workspace; none
is on a stopgap. What follows is the plan as executed, with the places reality
differed recorded inline — the card-level detail below is still the reference
for what each page reads and why.

| Role | Lands on | Phase |
|---|---|---|
| Cashier | `/cashier-dashboard` | 1 ✅ |
| StockController | `/stock-dashboard` | 2 ✅ |
| Manager | `/manager-dashboard` | 3 ✅ |

**Phase 0 was not done, and did not need to be.** It proposed extracting a
shared dashboard kit before adding pages. The three new pages instead reuse the
existing `dsh-` kit and `dashboard.css` directly, exactly as `AdminDashboard`
does, which got the no-duplication benefit without a refactor touching four
live dashboards. `DepotDashboard` landed with its own `dpd-` sheet and none of
the shared components, so a kit extraction would now be a bigger job for less
return than when it was written. Revisit only if a fifth page wants the chrome.

## Why this exists

The admin rebuild narrowed `/dashboard` to Admin (and SalesRep, who has always
been served a different component at that route). Three roles lost the page
they landed on and were sent to a working page as an interim — `/invoices`,
`/inventory-transfers` and `/reports`. That is a list, not a workspace. This
plan gave each of them a dashboard and flipped their landing route to it.

## Which roles get a dashboard

**Build one for:** Cashier, StockController, Manager.

**Already have one:** Admin (`/dashboard`), SalesRep (`/dashboard`, served by
`SalesRepDashboard`), DepotController (`/dashboard`, served by
`DepotDashboard`), PodOperator (`/pod-dashboard`).

> DepotController was originally planned here as a second reader of the stock
> dashboard. It got its own page first (#192), which settles the question: the
> stock dashboard is StockController's alone, and `DepotDashboard` is the
> nearer model to copy from since it already solves warehouse scoping off the
> `warehouse` claims.

**Do not build one for:** Driver (`/pods`), Merchandiser (`/mobile-drafts`),
Lab (`/lab/batch-status`), MerchandiserPurchaseOrderViewer
(`/reports/merchandiser-purchase-orders`). Each of these roles does one thing,
and their landing page is already the queue they work through — a summary in
front of it would be a click between them and their job. If one of them later
grows a second surface, revisit it then.

---

## Phase 0 — Extract the dashboard kit

Three dashboards exist today and share no code: `dashboard.css` (`dsh-`),
`sales-rep-dashboard.css` (`srd-`) and `pod-dashboard.css` each redraw the same
header, panel, stat and empty-state chrome. Adding three more without a shared
kit means six copies.

Run this **after** the admin rebuild, because that work already reshapes
`StatCard` and `DashIcon` — extract from the finished shape rather than the
current one.

Promote `Components/Dashboard/` into a role-agnostic kit:

- **`DashShell.razor`** — the page header: kicker/breadcrumb, greeting,
  standfirst, the "Read at HH:mm" stamp, and a `RenderFragment` for the header
  actions. Plus the body wrapper.
- **`StatCard`** — after the admin phase it already has optional `Amount` and a
  `Tone` (ok / warn / critical). Nothing more needed.
- **`DashPanel.razor`** — the panel shell (icon, heading, "View all" link,
  loading and empty states) that `ActivityTable` currently inlines. Each role's
  table then supplies only its own `<thead>`/`<tbody>`.
- **`QuickAction`**, **`DashIcon`** — unchanged, but every new glyph goes into
  `DashIcon` as an inline path. Never the Phosphor font on a dashboard: these
  pages paint before any icon font arrives, and a font-backed glyph leaves the
  figures headed by empty boxes on the render most people see.
- **`wwwroot/css/role-dashboard.css`** — the shared `dsh-` chrome, light and
  dark. Per-role sheets keep their own prefix and carry only what genuinely
  differs.

> Page stylesheets in this app are separated by prefix alone, not by scope. A
> duplicated prefix silently restyles another page. The kit owns `dsh-`; every
> role sheet must pick a prefix nothing else uses.

Also in this phase, so the nav has one rule instead of five:

- Add `RoleLandingRoutes.DashboardRoutes` (a set) and `IsDashboard(route)`.
- The nav's Overview section renders one **Dashboard** link at
  `RoleLandingRoutes.For(user)`, shown only when `IsDashboard` says that route
  is a dashboard. A Driver keeps no link; a PodOperator's points at
  `/pod-dashboard`; a Cashier's at `/cashier-dashboard` once Phase 1 lands.
- `Topbar.razor`'s brand href follows the same call (already changed in the
  admin phase).

---

## Phase 1 — Cashier

**Route** `/cashier-dashboard` · `[Authorize(Roles = "Admin,Cashier")]` ·
prefix `csd-`

The till: what has been raised, what has been taken, and what is stuck.

| Band | Card / panel | Source |
|---|---|---|
| Figures | Invoices raised today — count + value, vs yesterday | `IInvoiceService.GetInvoicesByDateRangeAsync` |
| | Payments received today — count + value, vs yesterday | `IPaymentService.GetPaymentsByDateAsync` |
| | Credit notes raised today | `ICreditNoteService` |
| Needs attention | Sales orders awaiting conversion to invoice | `ISalesOrderService.GetSalesOrdersAsync(status:)` |
| | Invoices that failed fiscalization | **confirm** — a service behind `/fiscal-transaction-log` |
| | Open invoices / balance owing | **confirm** — an open-invoices endpoint exists per `OpenInvoicesByCustomersTests` |
| Panels | Recent invoices, recent payments | reuse the tables from the current dashboard |
| Quick actions | Create invoice · Receive payment · Create sales order · Quotations · Credit notes | — |

Notes:

- The invoice day totals need the paging helper already written on `Home.razor`
  (`GetInvoiceDayTotalsAsync`): `TotalCount` is authoritative from a count
  query, but the value total needs following pages, capped at 10. Move it into
  a shared helper rather than copying it a third time.
- The payments date endpoint is unpaged — one call covers the day.
- Confirm the two **confirm** rows before committing to the layout. If either
  endpoint is missing, drop the card rather than inventing a figure; hand-written
  API URLs in this app fail silently as "not found".

**Ship with:** `RoleLandingRoutes` Cashier → `/cashier-dashboard`;
`RoleLandingRouteTests` row updated; nav link follows automatically from Phase 0.

---

## Phase 2 — Stock controller

**Route** `/stock-dashboard` ·
`[Authorize(Roles = "Admin,StockController")]` · prefix `stkd-`

The role is warehouse-scoped (`UserRoles.RequiresWarehouseAssignment`).
Resolve the warehouse with `DefaultWarehouseResolver.Resolve` from the user's
`warehouse` claims; if more than one is assigned, put a picker in the header
and key the whole page off it.

`DepotDashboard` already does exactly this scoping for the depot role — read it
before writing this page rather than solving the claims-to-warehouse problem a
second time.

| Band | Card / panel | Source |
|---|---|---|
| Warehouse | Items in stock / out of stock / committed / on order | `IWarehouseStockCacheService.GetStockSummaryAsync` |
| | Cache last synced, with a Sync action | `GetSyncStatusAsync` / `SyncWarehouseStockAsync` |
| Queues | Transfers awaiting approval, this warehouse | `GetPendingTransfersAsync(status, warehouseCode)` |
| | Transfers that failed to post | same call, `PendingTransferStatuses.PostFailed` |
| | Open transfer requests | `GetTransferRequestsAsync` — **first page only** |
| Panels | Recent transfers in/out | `GetTransfersByDateRangeAsync(warehouseCode, …)`, paged |
| | Items out of stock | `GetCachedStockAsync` filtered |
| Quick actions | Create transfer · Transfer requests · Local stock · Batches · Products | — |

Two rules this page must not break:

- **Count items, never sum quantities.** `GetStockSummaryAsync` already returns
  item counts, which is why it is the right source. Anything that adds a
  quantity column across items is adding crates to kilos to eaches. (The current
  dashboard's "Total units" card does exactly this; the admin phase removes it.)
- **Never walk the transfer-request pages.** There are roughly eleven thousand
  of them and each hundred costs 5–11 seconds. Take `TotalCount` from page one
  and stop.

Post-failed transfers earn a card of their own: a disconnect during posting can
strand a document with no error and no retry, and nothing surfaces that today.

**Ship with:** `RoleLandingRoutes` StockController → `/stock-dashboard`; its
`RoleLandingRouteTests` row updated. DepotController is not touched — it lands
on `/dashboard` and Home draws `DepotDashboard` there.

---

## Phase 3 — Manager

**Route** `/manager-dashboard` · `[Authorize(Roles = "Admin,Manager")]` ·
prefix `mgd-`

Oversight, not data entry: purchasing, the approval queues, and what the day
traded.

| Band | Card / panel | Source |
|---|---|---|
| Purchasing | Purchase requests pending | `IPurchaseRequestService` |
| | Purchase orders open / awaiting approval | `IPurchaseOrderService.GetPurchaseOrdersAsync(status:)` |
| | Goods receipts today; POs ordered but not received | `IGoodsReceiptPurchaseOrderService` |
| Queues | Transfers awaiting approval, all warehouses | `IInventoryTransferService.GetPendingTransfersAsync` |
| Trading | Invoices and payments today, vs yesterday | shared day-totals helper |
| People | Active users, total actions, failed actions today | `IAuditService.GetActivityStatsAsync` |
| Panels | Purchase documents needing a decision | purchasing services |
| | Recent activity | `IAuditService.GetLogPageAsync` |
| Quick actions | Purchase requests · Purchase orders · Reports · Inventory transfers · Activity log | — |

The trading figures are deliberately the same ones the admin dashboard keeps —
this is the band the Manager loses when `/dashboard` narrows, and it is the one
worth carrying over.

**Ship with:** `RoleLandingRoutes` Manager → `/manager-dashboard`;
`RoleLandingRouteTests` row updated.

---

## Rules every one of these follows

1. **Read once, stamp the time.** Each page reads on first render, sets
   `loadedAt` after `Task.WhenAll` completes, and shows "Read at HH:mm". No
   polling — the stamp is how a reader tells how old the figures are. If we ever
   add refresh, it goes in the kit once, not per page.
2. **Never show a zero that is about to jump.** Figures read "—" until their
   number lands, per `SalesRepDashboard`'s `Figure()` helper.
3. **One failing service must not blank the page.** Keep the per-read
   try/catch + `finally { StateHasChanged }` pattern — but log the exception.
   The current catches are silent, which is how a dead route reads as an empty
   card.
4. **Audit the view.** `AuditService.LogAsync(AuditActions.ViewDashboard,
   "<Role>Dashboard", null)` once per session on the page.
5. **Both themes, written together.** Every rule gets its light and dark form in
   the same pass. No `:global()` inside an inline `<style>` — it is invalid CSS
   and the browser drops the whole rule. Unique prefix per sheet.
6. **Parallel reads, paged sources.** `Task.WhenAll` across the cards; anything
   SAP-backed is paged and never walked.
7. **Tests are xunit on extracted helpers.** There is no bUnit in the project.
   Trend maths, warehouse resolution and any figure formatting come out as pure
   functions and get tested; the Razor does not.
8. **Verify on a real account of that role.** Run Web (5051) and API (5106)
   together. Screenshots need headless Chrome — both browser tools are blocked
   by the app's CSP. The local database has one user, so a role check that
   passes locally has not been tested.

## Per-phase checklist

- [ ] Page component (+ `.razor.cs` where it earns the split)
- [ ] Stylesheet, both themes, new prefix, linked from `App.razor`
- [ ] `RoleLandingRoutes.For` updated; route added to `DashboardRoutes`
- [ ] `RoleLandingRouteTests` rows updated
- [ ] New `DashIcon` glyphs as inline paths
- [ ] Checked in light and dark, at desktop and the 960px breakpoint
- [ ] Own PR

## Questions, answered by building

1. **The cashier's two "confirm" cards** went opposite ways. Fiscalisation is
   available — and its summary is computed over the whole filtered set before
   the handler pages, so a page size of one returns accurate day counts.
   Open invoices is not: `GetOpenInvoicesByCustomersAsync` returns null for an
   empty list and there is no company-wide equivalent, so the card was dropped
   rather than built from a fan-out across every customer.
2. **Where the new dashboards live** — each on its own route. Home's switch
   falls through to "everyone else is an Admin", and a fourth role would leave
   that fallback meaning nothing. `/dashboard` keeps its three (Admin, SalesRep,
   DepotController); the other three stand alone.
3. **The nav link** reads "Dashboard" for everyone, but resolves its href per
   role through `RoleLandingRoutes.For`. `UserRoles.DashboardNavRoles` lists who
   sees it, and a test walks that list asserting each role lands somewhere it
   can actually open — hard-coding `/dashboard` was offering a cashier a link
   their own authorize attribute refuses.
4. **Two figures could not be built as specified** and say so on the card
   rather than implying precision: open transfer requests are company-wide
   (there is no warehouse-scoped paged endpoint) and carry no total, so a full
   page reads "100+"; and the manager's request and receipt counts are windowed
   to seven days because those endpoints have no status filter.

Still open: **do Nocturne designs exist** for these three in the design
projects? The four earlier dashboards were imported from `.dc.html` designs;
these three were built on the shared kit instead. If a design turns up later it
is a re-skin, not a rebuild.
