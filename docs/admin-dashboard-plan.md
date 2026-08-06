# Admin dashboard — rebuild plan

Status: **built**, not yet verified against a real environment — see
"Still to verify" at the end.

## The problem

`/dashboard` is a cashier's page wearing an administrator's URL. It was
everyone's page, so it was built for the most common role, and now that it
serves only Admin and SalesRep the mismatch is the whole complaint:

1. **It reports someone else's day.** The three leading figures are invoices
   raised, payments received and the transfer queue; the primary action is
   "Create invoice"; the quick actions are create invoice, search invoices,
   products, prices. An administrator does not raise invoices.
2. **One figure is meaningless.** The pending-transfers card shows "Total
   units" — `Items.Sum(item => item.TotalQuantity)` at
   [Home.razor:281](../ShopInventory.Web/Components/Pages/Home.razor:281) —
   adding crates to kilograms to eaches across transfers with different units.
   It has never been a number.
3. **Nothing reports the system the admin runs.** SAP connectivity, the
   exception queue, stranded posts, locked accounts, cache freshness, backups —
   none of it is here, and each lives on a page you have to remember to open.
4. **Failures are invisible.** Five `catch { }` blocks with no logging. A dead
   endpoint renders as a zero that looks like a quiet day.
5. **No figure can say it is bad.** `StatCard` renders 0 blocked exceptions and
   400 blocked exceptions identically.

## What it becomes

An operations page: *is the system healthy, what is stuck, what needs a
decision, who is doing what.* Trading figures stay, demoted to a glance — the
Manager dashboard picks them up properly in its own phase.

### Structure

**Split the page.** `Home.razor` becomes a thin router — SalesRep renders
`<SalesRepDashboard>`, Admin renders a new `<AdminDashboard>` — and the admin
content gets its own `AdminDashboard.razor` + `.razor.cs`. `Home.razor` is
already 442 lines with all the loading logic inline; `SalesRepDashboard`
already splits 380/752 the same way.

| Band | Card | Source | Tone |
|---|---|---|---|
| System | Overall health + per-dependency checks | `ISystemHealthService.GetHealthAsync` → `Status`, `Dependencies.Checks[]` (`Name`, `Status`, `DurationMs`) | from `Status` |
| Stuck | Blocked exceptions | `IExceptionCenterService.GetDashboardAsync` → `Triage.BlockedCount` | critical if > 0 |
| | Stalled / retry overdue | `Triage.StalledCount`, `Triage.RetryOverdueCount` | warn if > 0 |
| | Oldest still open | `Triage.OldestOpenAtUtc`, `Triage.AgeOver7dCount` | critical if over 7d |
| Decisions | Transfers awaiting approval | `GetPendingTransfersAsync(AwaitingApproval)` — **count only** | neutral |
| | Transfers that failed to post | `GetPendingTransfersAsync(PostFailed)` | critical if > 0 |
| People | Active users, actions, failed actions today | `IAuditService.GetActivityStatsAsync` | warn on failures |
| | Locked accounts, users without 2FA | `IUserManagementService.GetSecurityStatsAsync` | warn if > 0 |
| Trading | Invoices and payments today, vs yesterday | the existing day-totals helpers, unchanged | neutral |

**Panels**

- **Top exception clusters** — `Clusters[]` gives `Label`, `Count`, `Family`
  and `Guidance`. The guidance string is the useful part: it says what to do.
- **Recent activity** — `IAuditService.GetLogPageAsync`, replacing one of the
  two recent-document tables.

**Quick actions** — Exception centre · Users · Sync status · Approval process ·
Backups · Settings. (Replacing create-invoice / search-invoices / products /
prices, which belong to the Cashier page.)

### Component changes

- **`StatCard`** — add `Tone` (`neutral` / `ok` / `warn` / `critical`), drawn
  as the card's rule and figure colour. Make `Amount` optional: it is
  `EditorRequired` today, and the transfers card only carried one to hold the
  bogus units figure. Tone must not be colour alone — pair it with the icon, as
  `Direction` already does with its arrow.
- **`DashIcon`** — new inline paths: `heartbeat`, `warning`, `shield`, `users`,
  `plug`, `database`. Inline paths, never the Phosphor font: the page paints
  before any icon font arrives and a font-backed glyph leaves the figures
  headed by empty boxes on the render most people see.
- **`dashboard.css`** — tone classes for the stat card, light and dark in the
  same pass.
- **Delete** `pendingTransferUnits` and its sum.
- **Log the swallowed exceptions** — inject `ILogger<AdminDashboard>` and log
  in each catch. Keep the graceful unavailable state; just stop hiding why.

### Also in this phase

- `Topbar.razor`'s brand href → `RoleLandingRoutes.For(user)`, so the logo
  takes each role home rather than always to `/dashboard`.

## What shipped

- `AdminDashboard.razor` + `.razor.cs` — the page above. `Home.razor` is now a
  thin router between it and `SalesRepDashboard` (442 lines down to 92).
- `StatCard` — `Tone` (via the new `StatTone` enum) and `ToneNote`; `Amount` is
  optional and the foot is omitted when a card has no secondary reading.
- `DashboardFigures` — the invoice/payment day totals and `BuildTrend` lifted
  out of Home so the cashier's dashboard can reuse them, with
  `DashboardFiguresTests` covering the trend cases that were unreachable while
  it was a private method.
- `DashIcon` — `heartbeat`, `warning`, `check-circle`, `shield`, `users`,
  `plug`, `database`, `clock` as inline paths.
- `dashboard.css` — `--dsh-warn-*` and `--dsh-crit-*` triples in both themes,
  and the `.dsh-stat-{ok,warn,critical}` rules. They sit after
  `.dsh-stat-lead` deliberately: the lead card can itself go critical and the
  two selectors tie on specificity, so source order is what makes the tint and
  the label colour win.
- `Topbar.razor` — the brand href follows `RoleLandingRoutes.For(user)`.

## Done already

- `RoleLandingRoutes.For` — Admin and SalesRep resolve to `/dashboard` first,
  then Cashier → `/invoices`, StockController → `/inventory-transfers`,
  Manager → `/reports`. Resolving Admin above the three keeps an administrator
  carrying one of those roles on the dashboard.
- `UserRoles.DashboardRoles` → `"Admin,SalesRep"`, which also drops the
  Dashboard link from the nav's Overview section and from Topbar search for the
  re-homed roles.
- `RoleLandingRouteTests` — rows updated, plus theories covering an Admin and a
  SalesRep who also carry a re-homed role. 27 tests pass.

## Still to verify

Both were flagged before building and neither can be settled from the local
database. The page is built so that either answer only costs a card, not the
layout.

1. **Exception-centre cost and triage completeness.**
   `GetDashboardAsync(limit: 20)` is what the page asks for. If the API
   computes `Triage` only over the items it returned, the blocked and stalled
   figures are floors rather than totals and the limit must go up — or the card
   must say "20+". Check a real response before trusting the number.
2. **Health-check names.** `Dependencies.Checks[]` is whatever the API
   registers. `HealthNote` names up to two failing checks and falls back to a
   count, so it does not depend on the list — but confirm the names read well
   in a card before leaving them raw.

Beyond those: the page has only been seen in a static harness (headless Chrome
against the real stylesheet, light and dark). It has not been opened by a real
Admin account against a real API.

## Rules

Same as the role dashboards ([role-dashboards-plan.md](role-dashboards-plan.md)):
read once and stamp the time, "—" until a figure lands, one failing service
must not blank the page, both themes written together, parallel reads, paged
sources, xunit on extracted helpers. Verify on a real admin account against the
reported environment — a role check that passes on the local single-user
database has not been tested.
