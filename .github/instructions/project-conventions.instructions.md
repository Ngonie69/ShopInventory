---
applyTo: "**"
---

# Project Conventions

## Timezone

All timestamps are stored as **UTC** in the database. Convert timestamps to **CAT (Central Africa Time, UTC+2)** with `AuditService.ToCAT()` only for user-facing output and for logs that are explicitly created for operator or audit review. Keep machine-oriented, system, and internal diagnostic logs in UTC. Never hardcode offsets — always use the `ToCAT()` helper. When creating new `DateTime` values, use `DateTime.UtcNow`, never `DateTime.Now`.

## Vertical Slice Architecture (CQRS + MediatR)

All features in **both projects** (API and Web) **must** follow the vertical slice architecture using CQRS with MediatR. Apply it with this checklist:

1. Put business logic in feature handlers, not controllers or standalone services.
2. Place writes under `Commands`, reads under `Queries`, and optional side effects under `Events`.
3. Follow the file, handler, validation, and controller rules below for the touched slice.

When modifying an existing feature that still uses the old `Services/` pattern, **migrate it to a vertical slice** as part of the change. Do not add new logic to legacy service classes.

### Feature folder structure

```
ShopInventory/Features/{Domain}/
  Commands/{Operation}/
    {Operation}Command.cs       # sealed record : IRequest<ErrorOr<TResult>>
    {Operation}Handler.cs       # sealed class with primary constructor : IRequestHandler
    {Operation}Validator.cs     # sealed class : AbstractValidator<TCommand> (optional)
  Queries/{Operation}/
    {Operation}Query.cs         # sealed record : IRequest<ErrorOr<TResult>>
    {Operation}Handler.cs       # sealed class with primary constructor : IRequestHandler
  Events/ (optional)
    {Event}.cs                  # INotification
    {Event}Handler.cs           # INotificationHandler
```

### Rules

#### Structure and types

- **One file per class.** No multiple types in a single file.
- **Commands** are for writes (create, update, delete). **Queries** are for reads.
- Use `sealed record` for commands and queries (immutable, value equality).
- Use `sealed class` with **primary constructors** for handlers and validators.

#### Handlers and data flow

- Return `ErrorOr<T>` from all handlers — never throw exceptions for business rule violations.
- Validation pipeline (`ValidationBehavior`) runs `FluentValidation` validators automatically — do not call validators manually.
- Queries **must** use `AsNoTracking()` and project directly to DTOs via `Select()`.
- Always pass `CancellationToken` through the entire call chain.

#### Controllers and errors

- Controllers are **thin dispatchers** — inject only `IMediator`, call `mediator.Send()`, and map results via `result.Match()`. No business logic in controllers.
- Controllers inherit from `ApiControllerBase` which provides `Problem(List<Error>)` for error-to-HTTP mapping.
- Domain-specific errors go in `ShopInventory/Common/Errors/Errors.{Domain}.cs`.

### Do NOT

- Add business logic to controllers.
- Create standalone service classes for new features — use handlers instead.
- Add new logic to legacy `Services/` classes — migrate the feature to a vertical slice.
- Return raw entities from handlers — always return DTOs or result records.
- Use `[ApiController]` on feature controllers — inherit `ApiControllerBase` instead.

## Deployment

**Always use `.\Update-Production.ps1`** to deploy to the production server (10.10.10.9). Never manually copy files, `dotnet publish` to production paths, or use `xcopy`/`robocopy` directly.

```powershell
.\Update-Production.ps1 -DeployTarget Both      # API + Web
.\Update-Production.ps1 -DeployTarget API        # API only
.\Update-Production.ps1 -DeployTarget Web        # Web only
.\Update-Production.ps1 -RestartOnly             # Restart IIS app pools only
```

The script handles: building in Release mode, stopping IIS app pools (`ShopInventoryAPI`, `ShopInventoryWeb`), backing up the current deployment, copying files while preserving `web.config` and secrets, and restarting pools. Do not bypass any of these steps.

When asked to deploy, run the script with the appropriate `-DeployTarget`. When generating deployment commands, always emit the `Update-Production.ps1` invocation.
