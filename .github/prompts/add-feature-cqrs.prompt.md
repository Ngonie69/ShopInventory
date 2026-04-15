---
description: "Add a new feature using CQRS pattern with MediatR. Separates commands (writes) from queries (reads) with proper validation, logging, and pipeline behaviors."
agent: "agent"
---

# Add Feature with CQRS + MediatR

Implement the requested feature using the **CQRS (Command Query Responsibility Segregation)** pattern with **MediatR** in the ShopInventory API project. All new business logic must flow through MediatR handlers — controllers remain thin dispatchers.

## Pre-flight: Ensure MediatR Is Installed

Before scaffolding, verify MediatR is installed in the API project:

1. Check `ShopInventory/ShopInventory.csproj` for these packages:
   - `MediatR` (latest stable for .NET 10)
   - `FluentValidation.DependencyInjectionExtensions` (for validation pipeline)
   - `ErrorOr` (for Result pattern — typed success/failure returns)
2. If missing, add them:
   ```xml
   <PackageReference Include="MediatR" Version="12.*" />
   <PackageReference Include="FluentValidation.DependencyInjectionExtensions" Version="11.*" />
   <PackageReference Include="ErrorOr" Version="2.*" />
   ```
3. Check `ShopInventory/Program.cs` for MediatR registration. If missing, add:
   ```csharp
   builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(Program).Assembly));
   builder.Services.AddValidatorsFromAssembly(typeof(Program).Assembly);
   ```
4. Check for the validation pipeline behavior in `ShopInventory/Behaviors/`. If missing, create it (see **Pipeline Behaviors** section below).

## Folder Structure

Place all CQRS artifacts under a domain-specific folder within `Features/`:

```
ShopInventory/
  Common/
    Errors/
      Errors.{Domain}.cs               # Domain-specific error constants
  Features/
    {DomainName}/                      # e.g., Invoices, Products, Payments
      Commands/
        Create{Entity}/
          Create{Entity}Command.cs         # IRequest<ErrorOr<TResponse>>
          Create{Entity}Handler.cs         # IRequestHandler<TRequest, ErrorOr<TResponse>>
          Create{Entity}Validator.cs       # AbstractValidator<TRequest>
        Update{Entity}/
          ...
        Delete{Entity}/
          ...
      Queries/
        Get{Entity}ById/
          Get{Entity}ByIdQuery.cs          # IRequest<ErrorOr<TResponse>>
          Get{Entity}ByIdHandler.cs        # IRequestHandler<TRequest, ErrorOr<TResponse>>
        List{Entities}/
          List{Entities}Query.cs
          List{Entities}Handler.cs
      Events/
        {Entity}Created/
          {Entity}CreatedEvent.cs           # INotification
          {Entity}CreatedHandler.cs         # INotificationHandler (one per side effect)
          SendFiscalNotificationHandler.cs  # Another handler for the same event
  Behaviors/
    ValidationBehavior.cs
    LoggingBehavior.cs
```

**Rules:**
- One file per class. No multiple types in a single file.
- Handler files contain ONLY the handler class — no DTOs, no validators.
- Group by feature slice (vertical), not by technical layer (horizontal).

## Command Pattern (Write Operations)

Commands represent **state-changing** operations (create, update, delete).

### Command Record

```csharp
namespace ShopInventory.Features.{Domain}.Commands.Create{Entity};

public sealed record Create{Entity}Command(
    // Include only the input properties needed — NOT entity IDs for creation
    string Name,
    decimal Quantity
    // ... other required fields
) : IRequest<ErrorOr<Create{Entity}Result>>;

public sealed record Create{Entity}Result(
    int Id,
    string Message
);
```

**Best practices:**
- Use `sealed record` for immutability and value equality.
- Always return `ErrorOr<T>` — never throw exceptions for business rule violations.
- Do NOT inject `HttpContext`, `ClaimsPrincipal`, or ASP.NET types into commands. Pass user info (username, userId) as command properties if needed.
- Commands must be serializable — no service references or complex objects.

### Domain Errors

Define domain-specific errors in `ShopInventory/Common/Errors/`:

```csharp
namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class {Entity}
    {
        public static Error NotFound(int id) =>
            Error.NotFound("{Entity}.NotFound", $"{Entity} with ID {id} was not found.");

        public static Error DuplicateName(string name) =>
            Error.Conflict("{Entity}.DuplicateName", $"A {entity} with name '{name}' already exists.");

        public static readonly Error InvalidQuantity =
            Error.Validation("{Entity}.InvalidQuantity", "Quantity must be greater than zero.");
    }
}
```

**Error type mapping** (used by controllers):
- `Error.NotFound` → `404 Not Found`
- `Error.Validation` → `400 Bad Request`
- `Error.Conflict` → `409 Conflict`
- `Error.Failure` → `500 Internal Server Error`
- `Error.Unauthorized` → `403 Forbidden`

### Command Handler

```csharp
namespace ShopInventory.Features.{Domain}.Commands.Create{Entity};

public sealed class Create{Entity}Handler(
    ApplicationDbContext db,
    IPublisher publisher,
    ILogger<Create{Entity}Handler> logger
    // inject only what this handler needs
) : IRequestHandler<Create{Entity}Command, ErrorOr<Create{Entity}Result>>
{
    public async Task<ErrorOr<Create{Entity}Result>> Handle(
        Create{Entity}Command request,
        CancellationToken cancellationToken)
    {
        // 1. Validate business rules — return errors, don't throw
        var existing = await db.Entities
            .AnyAsync(e => e.Name == request.Name, cancellationToken);

        if (existing)
            return Errors.{Entity}.DuplicateName(request.Name);

        // 2. Map command to entity
        var entity = new Entity
        {
            Name = request.Name,
            Quantity = request.Quantity
        };

        // 3. Persist
        db.Entities.Add(entity);
        await db.SaveChangesAsync(cancellationToken);

        // 4. Publish domain event (fire-and-forget side effects)
        await publisher.Publish(
            new {Entity}CreatedEvent(entity.Id, entity.Name),
            cancellationToken);

        logger.LogInformation("Created {Entity} {EntityId}", entity.Id);

        return new Create{Entity}Result(entity.Id, $"{Entity} created successfully");
    }
}
```

**Best practices:**
- Use **primary constructors** for dependency injection.
- Always pass `cancellationToken` to async calls.
- Use `ILogger<T>` with structured logging (Serilog message templates, not string interpolation).
- Handlers should be `sealed` — they are not designed for inheritance.
- One handler per command — no shared base handler classes.
- For SAP operations, inject `ISAPServiceLayerClient` — never call SAP directly.
- Use **NoTracking** for any read queries within a handler unless writing.

### Command Validator

```csharp
namespace ShopInventory.Features.{Domain}.Commands.Create{Entity};

public sealed class Create{Entity}Validator : AbstractValidator<Create{Entity}Command>
{
    public Create{Entity}Validator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Name is required")
            .MaximumLength(200).WithMessage("Name must not exceed 200 characters");

        RuleFor(x => x.Quantity)
            .GreaterThan(0).WithMessage("Quantity must be positive");
    }
}
```

**Best practices:**
- Validators run automatically via `ValidationBehavior` pipeline — no manual calls needed.
- Validate shape/format here (not-empty, length, range). Business rules go in the handler.
- For async validation (e.g., checking DB uniqueness), inject `ApplicationDbContext` and use `MustAsync`.
- Keep validation messages user-friendly — they may surface to the UI.

## Query Pattern (Read Operations)

Queries represent **data retrieval** with no side effects.

### Query Record

```csharp
namespace ShopInventory.Features.{Domain}.Queries.Get{Entity}ById;

public sealed record Get{Entity}ByIdQuery(int Id) : IRequest<ErrorOr<{Entity}Dto>>;
```

### Query Handler

```csharp
namespace ShopInventory.Features.{Domain}.Queries.Get{Entity}ById;

public sealed class Get{Entity}ByIdHandler(
    ApplicationDbContext db
) : IRequestHandler<Get{Entity}ByIdQuery, ErrorOr<{Entity}Dto>>
{
    public async Task<ErrorOr<{Entity}Dto>> Handle(
        Get{Entity}ByIdQuery request,
        CancellationToken cancellationToken)
    {
        var entity = await db.Entities
            .AsNoTracking()
            .Where(e => e.Id == request.Id)
            .Select(e => new {Entity}Dto
            {
                Id = e.Id,
                Name = e.Name
            })
            .FirstOrDefaultAsync(cancellationToken);

        return entity is not null
            ? entity
            : Errors.{Entity}.NotFound(request.Id);
    }
}
```

**Best practices:**
- Always use `AsNoTracking()` for queries.
- Project directly to DTOs via `Select()` — do not load full entities then map.
- Return `ErrorOr<T>` with domain errors for not-found — let the controller map to HTTP status.
- For SAP list queries, use header-only `$select` — do not fetch `DocumentLines` for grids.
- For paginated queries, accept `PageNumber` and `PageSize` in the query record.

## Controller Integration

Controllers become thin dispatchers. They only handle HTTP concerns:

```csharp
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class {Entity}Controller(IMediator mediator) : ControllerBase
{
    [HttpPost]
    [RequirePermission("Create{Entity}")]
    public async Task<IActionResult> Create(
        Create{Entity}Command command,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            value => CreatedAtAction(nameof(GetById), new { id = value.Id }, value),
            errors => Problem(errors)
        );
    }

    [HttpGet("{id:int}")]
    [RequirePermission("View{Entity}")]
    public async Task<IActionResult> GetById(
        int id,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new Get{Entity}ByIdQuery(id), cancellationToken);

        return result.Match(
            value => Ok(value),
            errors => Problem(errors)
        );
    }
}
```

**Rules:**
- Inject `IMediator` only — no business services in controllers.
- Use `[RequirePermission("...")]` for fine-grained access, `[Authorize(Roles = "...")]` for role gates.
- Use `result.Match()` to map `ErrorOr<T>` to HTTP responses — never unwrap `.Value` directly.
- Pass `CancellationToken` from the action method to `mediator.Send()`.
- If the command needs the current user, extract from `User.Identity` in the controller and include in the command record.

### ApiController Base with Error Mapping

Create a shared base controller in `ShopInventory/Controllers/ApiControllerBase.cs` that maps `ErrorOr` errors to HTTP problem details:

```csharp
using ErrorOr;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ShopInventory.Controllers;

[ApiController]
public class ApiControllerBase : ControllerBase
{
    protected IActionResult Problem(List<Error> errors)
    {
        if (errors.All(e => e.Type == ErrorType.Validation))
        {
            var modelState = new ModelStateDictionary();
            foreach (var error in errors)
                modelState.AddModelError(error.Code, error.Description);
            return ValidationProblem(modelState);
        }

        var firstError = errors[0];

        var statusCode = firstError.Type switch
        {
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Validation => StatusCodes.Status400BadRequest,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            ErrorType.Unauthorized => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status500InternalServerError
        };

        return Problem(statusCode: statusCode, title: firstError.Description);
    }
}
```

Controllers inherit from `ApiControllerBase` instead of `ControllerBase`.

## Pipeline Behaviors

### ValidationBehavior (required)

Create in `ShopInventory/Behaviors/ValidationBehavior.cs`:

```csharp
namespace ShopInventory.Behaviors;

public sealed class ValidationBehavior<TRequest, TResponse>(
    IEnumerable<IValidator<TRequest>> validators
) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (!validators.Any())
            return await next();

        var context = new ValidationContext<TRequest>(request);
        var failures = (await Task.WhenAll(
                validators.Select(v => v.ValidateAsync(context, cancellationToken))))
            .SelectMany(r => r.Errors)
            .Where(f => f is not null)
            .ToList();

        if (failures.Count > 0)
            throw new ValidationException(failures);

        return await next();
    }
}
```

Register in `Program.cs`:
```csharp
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
```

### LoggingBehavior (recommended)

Create in `ShopInventory/Behaviors/LoggingBehavior.cs`:

```csharp
namespace ShopInventory.Behaviors;

public sealed class LoggingBehavior<TRequest, TResponse>(
    ILogger<LoggingBehavior<TRequest, TResponse>> logger
) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var requestName = typeof(TRequest).Name;
        logger.LogInformation("Handling {RequestName}", requestName);

        var response = await next();

        logger.LogInformation("Handled {RequestName}", requestName);
        return response;
    }
}
```

Register in `Program.cs`:
```csharp
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
```

### Handle ValidationException Globally

Add or update exception-handling middleware to catch `FluentValidation.ValidationException` and return a `400 Bad Request` with structured errors:

```csharp
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        if (exception is ValidationException validationException)
        {
            context.Response.StatusCode = 400;
            context.Response.ContentType = "application/json";
            var errors = validationException.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());
            await context.Response.WriteAsJsonAsync(new { errors });
        }
    });
});
```

## Domain Events (INotification)

Use MediatR notifications for **side effects** that should happen after a command succeeds (e.g., sending emails, fiscalization, SignalR pushes, audit logging). This decouples the primary operation from secondary concerns.

### Event Record

```csharp
namespace ShopInventory.Features.{Domain}.Events.{Entity}Created;

public sealed record {Entity}CreatedEvent(
    int EntityId,
    string EntityName
) : INotification;
```

### Event Handler (one per side effect)

```csharp
namespace ShopInventory.Features.{Domain}.Events.{Entity}Created;

public sealed class Send{Entity}NotificationHandler(
    INotificationService notificationService,
    ILogger<Send{Entity}NotificationHandler> logger
) : INotificationHandler<{Entity}CreatedEvent>
{
    public async Task Handle(
        {Entity}CreatedEvent notification,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Sending notification for {Entity} {EntityId}",
            notification.EntityId);

        await notificationService.SendAsync(
            $"New {entity} created: {notification.EntityName}",
            cancellationToken);
    }
}
```

### Event Handler for Fiscalization (example)

```csharp
namespace ShopInventory.Features.Invoices.Events.InvoiceCreated;

public sealed class FiscalizeInvoiceHandler(
    IFiscalizationService fiscalizationService,
    ILogger<FiscalizeInvoiceHandler> logger
) : INotificationHandler<InvoiceCreatedEvent>
{
    public async Task Handle(
        InvoiceCreatedEvent notification,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Fiscalizing invoice {InvoiceId}", notification.InvoiceId);
        await fiscalizationService.FiscalizeAsync(notification.InvoiceId, cancellationToken);
    }
}
```

**Best practices:**
- One handler class per side effect — keeps responsibilities isolated and testable.
- Event handlers are fire-and-forget by default. If one fails, others still execute.
- Do NOT put core business logic in event handlers — only secondary/cross-cutting concerns.
- Events are published inside command handlers via `IPublisher.Publish()` AFTER the primary operation succeeds.
- For critical side effects (e.g., fiscalization), consider outbox pattern or background service instead of in-process notification handlers.
- Use existing project services (`IFiscalizationService`, `INotificationService`, `ISAPServiceLayerClient`) — don't duplicate.

### When to Use Events vs Direct Calls

| Scenario | Approach |
|----------|----------|
| Sending email after invoice creation | `INotification` event handler |
| Fiscalizing an invoice (critical) | Event handler OR background service (project-specific) |
| SignalR real-time notification | Event handler injecting `IHubContext<NotificationHub>` |
| Audit logging | Event handler injecting `IAuditService` |
| Validating stock before creating an invoice | Direct call in command handler (NOT an event) |
| Acquiring inventory locks | Direct call in command handler (NOT an event) |

## Coexistence with Existing Services

This project has 43+ existing services using direct DI. New features use CQRS; existing code is NOT refactored unless explicitly requested.

- **New features** → CQRS handlers under `Features/`
- **Existing services** → remain in `Services/`, injected as before
- Handlers MAY inject existing services (e.g., `ISAPServiceLayerClient`, `IInventoryLockService`) when they encapsulate complex shared logic
- Do NOT duplicate logic already in existing services — compose via injection

## Progressive Migration Guide

When explicitly asked to migrate an existing controller/service to CQRS:

1. **Identify the controller** and list all its action methods.
2. **Create a feature folder** under `Features/{Domain}/`.
3. **Migrate one action at a time** — start with the simplest read endpoint.
4. For each action:
   a. Create the Query/Command record.
   b. Move business logic from the service into the handler.
   c. Create a validator (for commands).
   d. Update the controller action to use `mediator.Send()`.
   e. Extract side effects into domain event handlers.
5. **After all actions are migrated**, the old service interface can be removed from DI — but only if no other service depends on it.
6. Keep the controller in `Controllers/` — only the backing logic moves to `Features/`.

**Do NOT migrate existing code unless the user explicitly requests it.** The CQRS pattern applies to new features only by default.

## Checklist Before Completing

- [ ] Command/Query records are `sealed record` with all required properties
- [ ] All commands and queries return `ErrorOr<T>` — no raw types or `IActionResult`
- [ ] Domain errors defined in `Common/Errors/Errors.{Domain}.cs`
- [ ] Handlers are `sealed class` using primary constructors
- [ ] Validators exist for all commands (queries only if they have complex input)
- [ ] `CancellationToken` is passed through the entire call chain
- [ ] No ASP.NET types (`HttpContext`, `IActionResult`) leak into handlers
- [ ] Controller inherits `ApiControllerBase` and uses `result.Match()` for responses
- [ ] `AsNoTracking()` used on all read queries
- [ ] Structured logging with message templates (no `$"..."` in log calls)
- [ ] File-per-class, organized under `Features/{Domain}/Commands|Queries|Events/`
- [ ] Domain events published for side effects (notifications, audit, fiscal)
- [ ] Event handlers are one-per-side-effect, injecting existing project services
- [ ] MediatR + FluentValidation + ErrorOr packages and registrations are present in `.csproj` and `Program.cs`
- [ ] Pipeline behaviors (Validation, Logging) are registered
- [ ] `ApiControllerBase` with `Problem(List<Error>)` helper exists
