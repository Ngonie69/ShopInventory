# ShopInventory — Project Guidelines

## Overview

Blazor Server + ASP.NET Core REST API for invoicing and inventory management, integrated with SAP Business One, REVMax fiscal devices, and payment gateways (PayNow, Innbucks, Ecocash). Two-project solution targeting .NET 10.0 on PostgreSQL.

| Project | Role | Port | IIS Pool |
|---------|------|------|----------|
| `ShopInventory/` | REST API + SignalR hub | 5106 | ShopInventoryAPI |
| `ShopInventory.Web/` | Blazor Server frontend | 5107 (IIS: 80) | ShopInventoryWeb |

## Build & Run

```powershell
# Restore + build
dotnet build ShopInventory.sln

# Run API (terminal 1)
cd ShopInventory; dotnet run          # http://localhost:5106

# Run Web (terminal 2)
cd ShopInventory.Web; dotnet run      # http://localhost:5051

# Publish release
dotnet publish ShopInventory/ShopInventory.csproj -c Release -o ./publish/api
dotnet publish ShopInventory.Web/ShopInventory.Web.csproj -c Release -o ./publish/web
```

No test project exists yet. Validate changes by building and manually testing.

## Deployment

**Always use `.\Update-Production.ps1`** to deploy to production (10.10.10.9). Never manually copy files.

```powershell
.\Update-Production.ps1 -DeployTarget Both      # API + Web
.\Update-Production.ps1 -DeployTarget API        # API only
.\Update-Production.ps1 -DeployTarget Web        # Web only
.\Update-Production.ps1 -RestartOnly             # Restart app pools without redeploying
```

See [DEPLOYMENT.md](../DEPLOYMENT.md) for full architecture, Docker setup, and prerequisites.
See [SECRETS.md](../SECRETS.md) for secrets management (user-secrets, env vars, .env).

## Architecture

```
Browser → Blazor Web (5107) → API (5106) → SAP B1 Service Layer (10.10.10.6:50000)
                ↓                  ↓              ↓
        WebAppDbContext      AppDbContext     REVMax fiscal (172.16.16.201:8001)
        (cache + audit)    (73 DbSets)       Payment gateways
```

### API project (`ShopInventory/`)

- **Controllers/** — 33 REST controllers. Authorization via `[Authorize(Policy = "ApiAccess")]` at class level, role restrictions per action.
- **Services/** — 43+ services. Key domains: SAP client, batch inventory validation (FIFO/FEFO), invoice queue, fiscalization, payment gateways, email queue.
- **Authentication/** — JWT Bearer + API Key (`X-API-Key` header). Permission-based authorization via `[RequirePermission("...")]`.
- **Middleware/** — Idempotency (duplicate request prevention), SAP concurrency limiter, security headers.
- **Models/Entities/** — EF Core entities. **Data/ApplicationDbContext.cs** has 73 DbSets on PostgreSQL.
- **Hubs/NotificationHub.cs** — SignalR for real-time notifications (user/role/broadcast groups).
- **Background services** — `InvoicePostingBackgroundService`, `InventoryTransferPostingBackgroundService`, `ReservationCleanupService`.

### Web project (`ShopInventory.Web/`)

- **Components/Pages/** — Blazor pages using `@rendermode InteractiveServer`.
- **Components/Layout/** — `MainLayout.razor` (sidebar nav, theme toggle, notifications), `NavMenu.razor` (role-gated links).
- **Services/** — 47+ services. Caching layer (`MasterDataCacheService`, `WarehouseStockCacheService`), theme, printer integration, Excel export.
- **Data/WebAppDbContext.cs** — Separate PostgreSQL DB for cached SAP data, audit logs, customer portal users.
- **wwwroot/app.css** — All custom CSS including dark mode (`.dark-theme` class on `<html>`).

### Key integration points

| System | Config section | Notes |
|--------|---------------|-------|
| SAP B1 Service Layer | `SAP:*` | TLS thumbprint whitelist in production; never `SkipCertificateValidation` outside dev |
| REVMax fiscal | `Revmax:*` | 15.5% VAT rate; duplicate prevention built in |
| Payment gateways | `PaymentGateways:*` | PayNow, Innbucks, Ecocash — all disabled by default |
| Customer portal | `CustomerPortal:*` | Separate JWT; `JwtSecret` required (≥32 chars) |

## Code Conventions

### C# / .NET

- **Nullable reference types** enabled; **implicit usings** enabled.
- Services follow interface + implementation pattern (`IXxxService` / `XxxService`), registered in `Program.cs`.
- Controllers return `IActionResult` with typed DTOs from `DTOs/` folder.
- Use `[RequirePermission("...")]` for fine-grained access control, `[Authorize(Roles = "...")]` for role gates.
- Database queries default to **NoTracking** (read-heavy workload). Use explicit tracking only for writes.
- Logging via **Serilog** — use `ILogger<T>`, structured logging with message templates.
- Time zone: all timestamps stored as UTC, display converted to CAT (UTC+2) via `AuditService.ToCAT()`.

### Blazor pages

- UI: **Bootstrap** for layout + grid, **MudBlazor** for interactive components (buttons, date pickers, dialogs, icons).
- CSS: page-scoped styles use prefixes (`inv-`, `prod-`, `stk-`, `so-`, `po-`, `pay-`, etc.) to avoid collisions.
- Dark mode: toggle `.dark-theme` class on `<html>` element; use CSS variables from `app.css`, not hardcoded colors.
- Auth state: `CustomAuthStateProvider` reads JWT from localStorage via `Blazored.LocalStorage`.
- Inject services via `@inject IXxxService XxxService` at page top.
- NavMenu links wrapped in `<AuthorizeView Roles="...">` for role gating.

### SAP integration

- All SAP calls go through `ISAPServiceLayerClient`. Never call SAP directly from controllers.
- Use header-only queries with `$select` for list/grid endpoints — avoid fetching `DocumentLines` for pagination.
- Paging: SAP count query + offset. Do not fetch full datasets for UI grids.
- Cache heavy reports using `ReportService` cache helpers (`TryGetCachedValue` / `CacheValue`).
- `SAPConcurrencyHandler` limits parallel SAP requests — respect the semaphore.

### Invoice creation flow (critical path)

1. Validate model → check idempotency key (`U_Van_saleorder`)
2. Validate quantities and warehouse codes
3. If `autoAllocateBatches`: run `IBatchInventoryValidationService` (FIFO/FEFO)
4. Acquire inventory locks via `IInventoryLockService`
5. Post to SAP → queue for batch processing → fiscalize via REVMax → generate PDF

### Database migrations

```powershell
# API project
cd ShopInventory
dotnet ef migrations add MigrationName
dotnet ef database update

# Web project
cd ShopInventory.Web
dotnet ef migrations add MigrationName
dotnet ef database update
```

**Constraints**: non-negative quantity constraints on all stock/batch columns. Always include these in new migration entities.

## Roles

Admin, User, Cashier, StockController, DepotController, Manager, PodOperator, Driver, Merchandiser, SalesRep, ApiUser

- **SalesRep**: restricted dashboard — no invoice/payment/transfer widgets. See repo memory `sales_rep_dashboard_access.md`.
- **Driver**: can only view PODs they uploaded (filtered by `UploadedByUserId`).

## Known Issues & Gotchas

- `IInventoryLockService` is in-memory — replace with Redis for multi-instance production.
- API-side audit logging model exists but is **not implemented** in any controller. Web-side audit covers create operations and auth events.
- Payment gateway callback verification (`VerifyCallbackSignature`) currently returns `true` — needs real implementation.
- Web app trusts all forwarded headers (`KnownNetworks.Clear`/`KnownProxies.Clear`) — restrict in production.
- `Update-Production.ps1` preserves target `web.config`; production secrets (SAP share creds, etc.) persist across deploys.
