# ShopInventory Project Instructions

This is the single canonical instruction file for ShopInventory. Read and apply it before starting any new feature, bug fix, refactor, deployment change, or documentation change.

## Project Baseline

ShopInventory is a .NET 10 solution with two primary applications:

- `ShopInventory/` - ASP.NET Core REST API, SignalR hub, background workers, PostgreSQL operational database, and integrations.
- `ShopInventory.Web/` - Blazor Server web application, web-side PostgreSQL cache/audit database, and UI workflow layer.

The workspace also contains `OpenWA/`, a separate NestJS WhatsApp gateway used only when WhatsApp features are enabled.

Key runtime flow:

```text
Browser -> ShopInventory.Web -> ShopInventory API -> PostgreSQL / SAP B1 / REVMax / payment gateways
```

Read `architecture.md` when the change touches architecture, integration boundaries, deployment behavior, background workers, or data ownership.

## Before Starting Any Change

1. Identify the affected project: API, Web, OpenWA, scripts, docs, or a combined change.
2. Inspect the existing domain before editing: relevant controller, feature slice, DTOs, entities, errors, services, page/component, and tests or manual validation paths.
3. Prefer existing repo patterns and local abstractions. Do not introduce parallel frameworks, duplicate pipeline behaviors, or alternate result patterns.
4. Keep the diff focused on the requested behavior. Do not migrate neighboring services, rewrite unrelated methods, or clean up unrelated files unless explicitly asked.
5. Protect secrets. Do not commit real credentials, production keys, certificates, tokens, or connection strings.

## Architecture Rules

All new feature work in both `ShopInventory/` and `ShopInventory.Web/` must use vertical slice architecture with CQRS and MediatR.

Use this shape:

```text
{Project}/Features/{Domain}/
  Commands/{Operation}/
    {Operation}Command.cs
    {Operation}Handler.cs
    {Operation}Validator.cs
    {Operation}Result.cs
  Queries/{Operation}/
    {Operation}Query.cs
    {Operation}Handler.cs
    {Operation}Validator.cs
    {Operation}Result.cs
  Events/
    {Event}.cs
    {Event}Handler.cs
```

Rules:

- Use commands for writes and state changes.
- Use queries for reads.
- Use events/notifications for secondary side effects when useful.
- Put business logic in handlers, not controllers, pages, or standalone service classes.
- Use one file per type.
- Use `sealed record` for commands and queries.
- Use `sealed class` with primary constructors for handlers and validators.
- Return `ErrorOr<T>` from handlers. Do not throw exceptions for expected business rule failures.
- Put domain errors in `ShopInventory/Common/Errors/Errors.{Domain}.cs` for API slices.
- Pass `CancellationToken` through the full async call chain.

When modifying existing behavior that still lives in legacy `Services/`, move only the touched business behavior into a vertical slice as part of the change. Existing services may remain for infrastructure clients, external integrations, queues, caches, browser/platform adapters, and background worker support.

## API Rules

Controllers are thin dispatchers:

- Inherit `ApiControllerBase`.
- Inject only `IMediator` unless existing controller structure requires otherwise.
- Apply route, authorization, and permission attributes at the controller or action level.
- Call `mediator.Send(...)`.
- Map results with `result.Match(...)` and `Problem(errors)`.
- Do not add `[ApiController]` to feature controllers because `ApiControllerBase` already provides it.
- Do not put business logic, SAP calls, EF queries, mapping orchestration, or validation logic in controllers.

Data and handler rules:

- Queries must use `AsNoTracking()` and project directly to DTOs or result records with `Select(...)`.
- Do not return raw EF entities from handlers.
- For writes, explicitly use tracking only where needed and call `SaveChangesAsync(cancellationToken)`.
- Use FluentValidation for request shape and format checks. Do not call validators manually.
- Keep data-dependent business rules in handlers.
- Use structured logging with `ILogger<T>` and message templates.
- Catch exceptions only when adding meaningful context or translating to a useful domain error.

## Web Rules

Blazor pages and components should stay focused on UI concerns:

- Markup and layout.
- Temporary UI state.
- Dialog open/close behavior.
- Navigation.
- Snackbar/toast feedback.
- Role-gated visibility with `AuthorizeView`.
- Page routing and `@attribute [Authorize]`.
- Grid paging and filter input state.

Workflow decisions, data loading, validation, mapping, auditing, cache refreshes, and coordination across API calls belong in Web feature handlers under `ShopInventory.Web/Features/{Domain}/`.

Web conventions:

- Use Bootstrap for page structure and grid layout.
- Use MudBlazor for interactive controls such as buttons, dialogs, date pickers, and icons.
- Prefer `.razor.cs` partial classes when a page is already large or behavior is non-trivial.
- Keep role-gated links in `NavMenu.razor` wrapped in `AuthorizeView`.
- Use the existing `CustomAuthStateProvider` and `Blazored.LocalStorage` auth pattern.
- API transport should use the existing scoped `HttpClient` or existing thin adapter services.

Dark mode is required for every touched page, component, dialog, and page-scoped style:

- Use the `.dark-theme` class on `<html>`.
- Prefer CSS variables from `ShopInventory.Web/wwwroot/app.css`.
- Use page-scoped CSS prefixes such as `inv-`, `prod-`, `stk-`, `so-`, `po-`, `pay-`, `sec-`, `cust-`, `ua-`, `um-`, and `notif-`.
- Add `.dark-theme .{page-prefix}-*` overrides in `app.css` when page-local styles or hardcoded colors need theme treatment.
- Do not ship light-only UI for a touched surface.

For long-running UI work, use cancellation and cleanup where needed. Cancel component-level work on dispose and unsubscribe SignalR or event handlers in `Dispose` or `DisposeAsync`.

## Integration Rules

SAP Business One:

- All SAP calls go through `ISAPServiceLayerClient` or an existing integration abstraction.
- Never call SAP directly from controllers or pages.
- Use narrow `$select` queries for list/grid endpoints.
- Avoid fetching `DocumentLines` or full documents for pagination and grid views.
- Use SAP count plus offset for paging.
- Respect `SAPConcurrencyHandler` and existing integration throttling.

Critical invoice path:

1. Validate request and permissions.
2. Check idempotency, including `U_Van_saleorder` where applicable.
3. Validate quantities and warehouse codes.
4. Use `IBatchInventoryValidationService` for FIFO/FEFO batch allocation when `autoAllocateBatches` applies.
5. Acquire inventory or workflow locks through existing lock abstractions.
6. Post to SAP, queue downstream work, fiscalize through REVMax, and generate PDFs according to the existing flow.

Other integrations:

- Keep REVMax fiscalization behind existing services and feature slices.
- Keep PayNow, Innbucks, and Ecocash behind payment gateway abstractions.
- Keep WhatsApp session mechanics inside `OpenWA`; the .NET API remains the policy and orchestration layer.
- Preserve health, readiness, and deployment-safe startup behavior.

## Data, Time, and Migrations

- Store timestamps as UTC.
- Use `DateTime.UtcNow`, never `DateTime.Now`, for new timestamps.
- Convert timestamps to CAT only for user-facing output or explicit audit/operator review with `AuditService.ToCAT()`.
- Keep machine-oriented logs and internal diagnostics in UTC.
- Never hardcode timezone offsets.
- Database queries default to no tracking for reads.
- New stock or batch quantity columns must preserve non-negative constraints.
- Add EF migrations in the project that owns the changed `DbContext`.

Migration commands:

```powershell
cd ShopInventory
dotnet ef migrations add MigrationName
dotnet ef database update

cd ShopInventory.Web
dotnet ef migrations add MigrationName
dotnet ef database update
```

## Security and Authorization

- API authentication uses JWT bearer auth and API key support.
- Use `[Authorize(Policy = "ApiAccess")]` for API access where consistent with existing controllers.
- Use `[RequirePermission("...")]` for fine-grained API permissions.
- Use role gates with `[Authorize(Roles = "...")]` or Blazor `AuthorizeView` where existing patterns do.
- Important create, update, delete, approval, settings, and security actions should preserve audit behavior.
- Audit failures should not break the main user workflow unless existing code treats them as required.
- Never bypass certificate validation outside development.

## Build and Validation

Default validation:

```powershell
dotnet build ShopInventory.sln
```

If the change is tightly scoped, building the affected project is acceptable:

```powershell
dotnet build ShopInventory/ShopInventory.csproj
dotnet build ShopInventory.Web/ShopInventory.Web.csproj
```

There is currently no dedicated test project in this repo. Report the build performed and any manual verification. For Web UI changes, manually check the touched surface in light mode and dark mode.

Run locally when needed:

```powershell
cd ShopInventory
dotnet run

cd ShopInventory.Web
dotnet run
```

## Deployment

Production deployments must use `.\Update-Production.ps1`. Never manually copy files, publish directly into production paths, or use `xcopy`/`robocopy` as a deployment shortcut.

```powershell
.\Update-Production.ps1 -DeployTarget Both
.\Update-Production.ps1 -DeployTarget API
.\Update-Production.ps1 -DeployTarget Web
.\Update-Production.ps1 -RestartOnly
```

The script handles Release builds, IIS app pool stop/start, backup, file copy, `web.config` preservation, secrets preservation, slot warm-up, and readiness checks.

## Definition of Done

Before finishing a feature or change:

- The affected domain was inspected before editing.
- New or changed business behavior lives in the correct feature slice.
- Controllers and Blazor pages remain thin.
- Commands, queries, handlers, validators, result records, and errors follow the project conventions.
- Queries use `AsNoTracking()` and projection.
- Expected business failures return domain errors through `ErrorOr<T>`.
- Timestamps and timezone conversion follow the UTC/CAT rules.
- Touched Web UI preserves dark-mode parity.
- Security, authorization, audit, integration, and deployment constraints are preserved.
- Documentation is updated when behavior, setup, deployment, or public API contracts change.
- The affected project or solution builds, or the reason validation could not run is clearly reported.
