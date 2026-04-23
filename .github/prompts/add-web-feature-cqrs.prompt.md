---
description: "Add a new ShopInventory.Web feature using CQRS, MediatR, vertical slices, and Blazor Server .NET 10 practices"
agent: "agent"
---

# Add New Web Feature with CQRS + MediatR + Vertical Slice

Implement the requested feature in `ShopInventory.Web/` using the repository's vertical-slice architecture. New business logic belongs in MediatR handlers, not in `.razor` pages and not in legacy `Services/` classes.

## Start Here

1. Inspect the current Web surface area first.
   - The page or component under `Components/Pages/`
   - The current service class under `Services/`
   - Any models under `Models/`
   - Any local persistence under `Data/WebAppDbContext.cs`
2. Decide whether the feature is:
   - Web-only UI or client-state behavior
   - An API-backed workflow
   - A combined API + Web change
3. If the touched behavior currently lives in a page or a legacy Web service, move the orchestration and business rules into a feature slice as part of the change.
4. If the feature spans both projects, implement the API slice first, then the Web slice that consumes it.

## Current Web Baseline

- `ShopInventory.Web` targets `net10.0`.
- It is a Blazor Server app using `@rendermode InteractiveServer`.
- Auth is handled by `CustomAuthStateProvider` with JWT stored in `Blazored.LocalStorage`.
- API transport uses the scoped `HttpClient` configured from the named client `ShopInventoryApi`.
- The project is still service-heavy today. New feature work must move toward vertical slices instead of adding more page logic or more business logic to `Services/`.

## Bootstrap MediatR in Web First

Before implementing the first Web slice, ensure the Web project has the same CQRS infrastructure baseline as the API project.

### Required packages in `ShopInventory.Web/ShopInventory.Web.csproj`

Use the same versions already standardized in the API project:

```xml
<PackageReference Include="ErrorOr" Version="2.0.1" />
<PackageReference Include="FluentValidation.DependencyInjectionExtensions" Version="12.1.1" />
<PackageReference Include="MediatR" Version="14.1.0" />
```

### Required registrations in `ShopInventory.Web/Program.cs`

Add these if missing:

```csharp
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(Program).Assembly));
builder.Services.AddValidatorsFromAssembly(typeof(Program).Assembly);
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
```

### Required behaviors in `ShopInventory.Web/Behaviors/`

If the Web project does not yet have them, add:

- `ValidationBehavior.cs`
- `LoggingBehavior.cs`

Mirror the API implementations unless the user explicitly asks for a different behavior.

## Non-Negotiable Rules

- Use vertical slices under `ShopInventory.Web/Features/{Domain}/`.
- Use one file per type.
- Use `sealed record` for commands and queries.
- Use `sealed class` with primary constructors for handlers and validators.
- Return `ErrorOr<T>` from handlers.
- Keep `.razor` pages thin. Pages should manage rendering, UI state, dialogs, navigation, and user feedback only.
- Do not put business rules, orchestration, mapping, or integration decisions in pages.
- Do not add new business logic to legacy `Services/` classes.
- Use `DateTime.UtcNow` for timestamps. Convert to CAT only for display or human-facing text through `AuditService.ToCAT()`.
- Pass `CancellationToken` throughout the slice when the call chain supports it.
- Use structured logging via `ILogger<T>`.
- If a handler reads from `WebAppDbContext`, queries must use `AsNoTracking()` and direct projection.

## Web Slice Layout

```text
ShopInventory.Web/
  Behaviors/
    ValidationBehavior.cs
    LoggingBehavior.cs
  Features/
    {Domain}/
      Commands/
        {Operation}/
          {Operation}Command.cs
          {Operation}Handler.cs
          {Operation}Validator.cs
          {Operation}Result.cs           # optional
      Queries/
        {Operation}/
          {Operation}Query.cs
          {Operation}Handler.cs
          {Operation}Validator.cs        # optional
          {Operation}Result.cs           # optional
      Events/
        {Event}.cs
        {Event}Handler.cs
```

Use domain-oriented folders, not horizontal layer folders.

## What Stays in Pages

Keep only UI concerns in `.razor` files or their code-behind partials:

- Markup and layout
- Temporary UI state
- Dialog open or close logic
- Navigation
- Snackbar or toast display
- Role-based visibility with `AuthorizeView`
- `@attribute [Authorize]` and page routing
- File upload widget wiring
- Grid paging state and filter input state

Anything that decides what to load, how to validate a workflow, how to transform data, when to audit, or how to coordinate multiple services belongs in a command or query handler.

## What Existing Services May Still Do

The Web project already has many `Services/` classes. During migration, those existing services may remain only as thin adapters for:

- API transport via `HttpClient`
- `Blazored.LocalStorage`
- `IJSRuntime`
- printer integration
- SignalR client wiring
- browser file upload plumbing
- cache wrappers or local persistence helpers

New handlers may inject these adapter services temporarily if they already exist, but do not add new business decisions to them.

If you touch a service-heavy feature, move the orchestration into handlers and leave the service as a narrow transport or platform adapter only when that is still useful.

## Component Pattern

Pages should dispatch commands and queries through MediatR.

### Preferred page shape

```razor
@page "/products"
@attribute [Authorize]
@inject IMediator Mediator
@inject NavigationManager Navigation
@inject ISnackbar Snackbar
@rendermode InteractiveServer
```

### Preferred event flow

```csharp
private async Task SaveAsync()
{
    var result = await Mediator.Send(new CreateProductCommand(ItemCode, ItemName));

    result.Match(
        value =>
        {
            Snackbar.Add("Product created.", Severity.Success);
            Navigation.NavigateTo($"/products/{value.Id}");
            return value;
        },
        errors =>
        {
            Snackbar.Add(errors[0].Description, Severity.Error);
            return default;
        });
}
```

For complex pages, prefer a `.razor.cs` partial class so the markup stays readable.

## Handler Expectations

- Handlers own feature behavior.
- Use `ErrorOr<T>` for expected failures.
- Validate request shape with FluentValidation.
- Put business rules and workflow coordination in the handler.
- For local reads, use `WebAppDbContext` with `AsNoTracking()` and `Select(...)`.
- For API-backed behavior, call the API through the existing scoped `HttpClient` or a thin existing adapter service.
- Log important transitions and unexpected failures with structured logging.
- Catch exceptions only when you can translate them into a useful domain error or add meaningful context.

## Queries in Web

Use queries for:

- loading page models
- loading dropdown data
- searching or filtering datasets
- reading local cached data
- combining multiple API results into a single page-facing result

Query handlers should return page-facing DTOs or result records, not raw EF entities.

## Commands in Web

Use commands for:

- form submission
- local persistence updates
- API-backed create, update, delete, approve, sync, or retry actions
- workflows that also require audit logging, cache refresh, or UI refresh signals

Publish notifications only for secondary side effects after the main command succeeds.

## Validation Standard

- Use FluentValidation validators per command, and for complex query input when needed.
- Keep input shape validation in validators.
- Keep data-dependent business rules in handlers.
- Do not call validators manually.

## Authorization and Audit

- Protected pages should continue to use `@attribute [Authorize]`.
- Role-gated UI stays in `AuthorizeView` blocks.
- Important create, update, delete, approval, security, and settings actions should continue to go through `IAuditService`.
- Audit failures must not break the main user workflow.

## UI Conventions

When adding or updating a page, preserve the current ShopInventory.Web design system:

- Bootstrap for page structure and grid layout
- MudBlazor for interactive controls such as buttons, dialogs, date pickers, and icons
- Page-scoped CSS prefixes such as `inv-`, `prod-`, `stk-`, `so-`, `po-`, `pay-`, `sec-`, `cust-`, `ua-`, `um-`, `notif-`
- Dark mode via the `.dark-theme` class on `<html>` and CSS variables in `wwwroot/app.css`
- Avoid hardcoded theme-dependent colors when CSS variables already exist

Keep UI behavior consistent on desktop and mobile.

## Long-Running UI Work

For pages that load data on initialize, poll, or subscribe to notifications:

- use a component-level `CancellationTokenSource` when helpful
- cancel on dispose
- unsubscribe SignalR or event handlers in `Dispose` or `DisposeAsync`
- avoid updating disposed components

## When a Feature Spans API and Web

Implement both sides as slices.

1. Create or update the API command or query under `ShopInventory/Features/`.
2. Add or update the controller action as a thin dispatcher.
3. Create the Web command or query under `ShopInventory.Web/Features/`.
4. Keep the page thin and dispatch through `IMediator`.

Do not solve a cross-project feature by adding more orchestration to a Web service class.

## Definition of Done

- MediatR, ErrorOr, and FluentValidation are present in the Web project if this is the first Web slice.
- The feature lives under `ShopInventory.Web/Features/{Domain}/`.
- Pages dispatch through `IMediator`.
- Business logic no longer lives in the touched page.
- New business logic is not added to a legacy `Services/` class.
- Validators exist for commands with meaningful inputs.
- Local queries use `AsNoTracking()` and projection.
- UTC timestamp handling is correct.
- UI follows the repo's Bootstrap, MudBlazor, prefix, and dark-mode conventions.
- The affected project builds successfully.
- Manual verification is reported because there is no test project yet.

## Working Style

- Make the code changes, not just an architectural proposal.
- Keep diffs focused.
- Preserve existing user workflows and page routes unless the request requires a route change.
- If a page is already large, prefer extracting handler-driven behavior plus a code-behind partial instead of growing the page further.