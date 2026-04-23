---
description: "Add a new ShopInventory feature using CQRS, MediatR, vertical slices, and repo-aligned .NET 10 practices"
agent: "agent"
---

# Add New Feature with CQRS + MediatR + Vertical Slice

Implement the requested feature in ShopInventory using the repository's vertical-slice architecture. Use MediatR for dispatching, ErrorOr for business outcomes, FluentValidation for request validation, and the .NET 10 / EF Core 10 patterns already used in this solution.

## Start Here

1. Identify the target project.
   - Default to `ShopInventory/` for API endpoints, background processing, and server-side business logic.
   - Use `ShopInventory.Web/` only when the user explicitly asks for a Blazor/Web feature. If the Web project must use MediatR and vertical slices, add the same infrastructure pattern there before implementing the slice.
2. Inspect the existing domain before changing code.
   - Relevant controller in `Controllers/`
   - Existing slices in `Features/{Domain}/`
   - Existing errors in `Common/Errors/Errors.{Domain}.cs`
   - Existing DTOs, entities, and integration services already used by that domain
3. If the touched feature still uses the old `Services/` pattern, migrate the touched behavior into a vertical slice as part of the change. Do not add new business logic to legacy service classes.

## Current Repo Baseline

- Both projects target `net10.0`.
- The API project already has:
  - `MediatR` `14.1.0`
  - `ErrorOr` `2.0.1`
  - `FluentValidation.DependencyInjectionExtensions` `12.1.1`
  - `AddMediatR`, `AddValidatorsFromAssembly`, `ValidationBehavior`, and `LoggingBehavior` registered in `Program.cs`
- Reuse what exists. Do not add parallel abstractions, duplicate pipeline behaviors, or alternate result patterns.

## Non-Negotiable Rules

- Use vertical slices under `Features/{Domain}/Commands`, `Queries`, and optionally `Events`.
- Use one file per type.
- Use `sealed record` for commands and queries.
- Use `sealed class` with primary constructors for handlers and validators.
- Return `ErrorOr<T>` from handlers. Do not throw for business rule failures.
- Keep controllers thin. Controllers inherit `ApiControllerBase`, inject `IMediator`, call `mediator.Send()`, and map with `result.Match(...)`.
- Do not add `[ApiController]` to feature controllers. `ApiControllerBase` already provides it.
- Put domain errors in `Common/Errors/Errors.{Domain}.cs`.
- Queries must use `AsNoTracking()` and project directly with `Select(...)`.
- Pass `CancellationToken` through the full call chain.
- Use structured logging with `ILogger<T>`.
- Use `DateTime.UtcNow` for timestamps. Convert to CAT for display or human-facing logs through `AuditService.ToCAT()`.
- Do not return EF entities from handlers. Return DTOs or dedicated result records.
- Do not create new standalone `Services/` classes for new business logic.
- For SAP work, go through `ISAPServiceLayerClient` or an existing integration abstraction. Never call SAP directly from controllers.

## Slice Layout

```text
ShopInventory/
  Common/
    Errors/
      Errors.{Domain}.cs
  Features/
    {Domain}/
      Commands/
        {Operation}/
          {Operation}Command.cs
          {Operation}Handler.cs
          {Operation}Validator.cs        # optional when the command has input validation
          {Operation}Result.cs           # optional when not reusing an existing DTO
      Queries/
        {Operation}/
          {Operation}Query.cs
          {Operation}Handler.cs
          {Operation}Validator.cs        # optional for complex query input
          {Operation}Result.cs           # optional when not reusing an existing DTO
      Events/
        {Event}.cs
        {Event}Handler.cs
```

Controller classes stay in `ShopInventory/Controllers/`.

If the user explicitly requests a Web-project slice, mirror the same structure under `ShopInventory.Web/Features/{Domain}/` and keep Blazor pages/components thin.

## Implementation Standard

1. Model the operation correctly.
   - Use a command for create, update, delete, and any state change.
   - Use a query for reads.
2. Create or extend `Errors.{Domain}.cs` early so failure paths are explicit.
3. Add request and response types.
   - Keep request contracts small and intentional.
   - Reuse an existing DTO when it already matches the response shape.
   - Otherwise create a dedicated result record or DTO.
4. Implement the handler.
   - Inject only the dependencies needed by that slice.
   - Use EF Core directly for straightforward data access.
   - Inject an existing service only when it encapsulates real shared or integration logic.
   - For read work inside a command, still use `AsNoTracking()` unless you are about to modify the loaded entity.
   - For writes, explicitly opt into tracking where needed.
5. Add a validator.
   - Put shape and format checks in FluentValidation.
   - Leave business-rule checks that depend on current data or integrations in the handler.
   - Do not call validators manually; the pipeline already runs them.
6. Extract secondary side effects into MediatR notifications when appropriate.
   - Notifications
   - Audit logging
   - SignalR pushes
   - Emails
   - Non-critical downstream actions
   Keep core transactional behavior in the command handler.
7. Wire the controller action.
   - Route, authorize, and permission-gate at the controller or action level.
   - Dispatch one command or query.
   - Map with `result.Match(...)`.
   - No business logic, mapping services, or SAP calls in the controller.
8. Validate with a build.
   - Run `dotnet build ShopInventory.sln`.
   - If the change is isolated, building the affected project is acceptable.
   - Note that there is no test project yet, so mention any manual verification performed.

## Handler Expectations

- Prefer straightforward async EF Core code over clever abstractions.
- Use `FirstOrDefaultAsync`, `SingleOrDefaultAsync`, `AnyAsync`, and `ToListAsync` with the provided `CancellationToken`.
- Project query results directly in the database query.
- Keep logging focused on important state transitions or failures.
- Return `Errors.{Domain}.*` for expected failures.
- Catch exceptions only when you can translate them into a meaningful domain error or add important logging or context. Do not swallow unexpected failures silently.

## Controller Pattern

Use the existing API pattern:

```csharp
[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class ProductsController(IMediator mediator) : ApiControllerBase
{
    [HttpPost]
    [RequirePermission("products.create")]
    public async Task<IActionResult> Create(
        [FromBody] CreateProductRequest request,
        CancellationToken cancellationToken)
    {
        var command = new CreateProductCommand(
            request.ItemCode,
            request.ItemName,
            User.Identity?.Name ?? string.Empty);

        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            value => CreatedAtAction(nameof(GetById), new { id = value.Id }, value),
            errors => Problem(errors));
    }
}
```

Use `ApiControllerBase` from `Controllers/ApiControllerBase.cs` for error-to-HTTP mapping.

## Query Pattern

- Query handlers return `ErrorOr<TResponse>`.
- Use `AsNoTracking()`.
- Use `Where(...)` plus `Select(...)` to shape the response in SQL.
- Support paging, filtering, and sorting explicitly in the query record when needed.
- For SAP-backed lists, prefer narrow projections and avoid fetching line or detail collections for grid endpoints.

## Command Pattern

- Commands own state changes.
- Validate preconditions and business rules before persisting.
- Use `SaveChangesAsync(cancellationToken)`.
- Publish notifications only after the main write succeeds.
- If the feature touches stock, reservations, invoicing, or fiscalization, respect the existing inventory lock, queue, and integration patterns already used in the repo.

## SAP and Integration Rules

- Use existing integration services where the repo already has one.
- Respect current performance guidance:
  - Use narrow selects.
  - Avoid loading full SAP documents for list views.
  - Do not bypass concurrency controls.
- If raw SAP SQL is unavoidable, keep it minimal, parameter-safe, and consistent with the repo's current SAP access patterns.

## .NET 10 and C# Practices

- Keep nullable assumptions explicit.
- Prefer file-scoped namespaces.
- Prefer primary constructors for handlers and validators.
- Prefer clear, allocation-light LINQ and EF queries over unnecessary intermediate collections.
- Use collection expressions or target-typed `new` only when they improve readability.
- Avoid static mutable state and request-specific singletons.
- Do not introduce reflection-heavy infrastructure or generic base handlers for simple CRUD slices.

## When Modifying Existing Features

- If the controller already dispatches MediatR requests, stay within the existing slice structure.
- If the controller still depends on a legacy service for the touched behavior, move that behavior into a new command or query handler and update the controller to dispatch it.
- Migrate only the behavior you are touching unless the request explicitly asks for a broader refactor.

## Definition of Done

- The feature sits under the correct domain slice.
- Commands and queries are named clearly and live in separate folders.
- Errors are defined in `Errors.{Domain}.cs`.
- Handlers return `ErrorOr<T>`.
- Controllers remain thin and dispatch through `IMediator`.
- Validators exist for commands with meaningful input rules.
- Queries use `AsNoTracking()` and projection.
- Timestamps use UTC.
- The affected project or solution builds successfully.
- Any manual verification steps are reported back to the user.

## Working Style

- Make the code changes, not just an architectural proposal.
- Keep diffs focused and consistent with the existing codebase.
- Update documentation only when the feature changes behavior, contracts, or setup.
