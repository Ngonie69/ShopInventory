---
description: "Implement API-side audit logging for admin and destructive operations across ShopInventory controllers"
agent: "agent"
---

# API-Side Audit Logging

Implement audit logging in the **ShopInventory API project** for admin and destructive operations. The API already has the `AuditLog` entity in `ShopInventory/Models/Entities/AuditLog.cs` and a `DbSet<AuditLog>` in `ApplicationDbContext`, but **no audit service or controller logging exists**.

## Context

- The **Web project** has a fully working `IAuditService` / `AuditService` in `ShopInventory.Web/Services/AuditService.cs` — use it as the reference pattern.
- The Web-side `AuditActions` class defines 39 action constants (Login, CreateInvoice, etc.) — reuse or mirror these on the API side.
- All timestamps must be stored as **UTC**. Display conversion to CAT (UTC+2) happens at the presentation layer.
- The API uses **Serilog** (`ILogger<T>`) for structured logging — audit logging is a separate, DB-persisted concern.

## Requirements

Apply the requirements in this order: first add the audit service, action constants, and DI registration; then wire controller coverage; then enforce the audit-call pattern and failure handling.

1. **Create `IAuditService` + `AuditService`** in `ShopInventory/Services/` following the Web project's interface pattern:
   - `LogAsync(action, username, userRole, entityType?, entityId?, details?, ipAddress?, isSuccess, errorMessage?)`
   - Simple overload that resolves the current user from `IHttpContextAccessor`
   - IP address extraction from `HttpContext.Connection.RemoteIpAddress` (respect `X-Forwarded-For`)

2. **Create `AuditActions` constants** in `ShopInventory/Models/` mirroring the Web-side constants but scoped to API operations.

3. **Register** the service in `Program.cs` as `AddScoped<IAuditService, AuditService>()`.

4. **Add audit logging to these controller categories** (admin/destructive only — skip read endpoints):
   - `AuthController` — Login, LoginFailed, Logout, RefreshToken
   - `InvoiceController` — CreateInvoice, CancelInvoice
   - `IncomingPaymentController` — CreatePayment
   - `CreditNoteController` — CreateCreditNote
   - `SalesOrderController` — CreateSalesOrder, CancelSalesOrder
   - `PurchaseOrderController` — CreatePurchaseOrder
   - `InventoryTransferController` — CreateTransfer
   - `UserController` / `UserManagementController` — CreateUser, UpdateUser, DeleteUser, ResetPassword, ChangeRole
   - `BackupController` — CreateBackup, RestoreBackup
   - `SAPSettingsController` — UpdateSettings
   - `PaymentController` — ProcessPayment, RefundPayment
   - `CustomerPortalController` — RegisterCustomer, DeactivateCustomer

5. **Do NOT** add audit logging to read-only endpoints (GET list/detail queries, health checks, price lookups).

6. **Log pattern** per action:
   ```csharp
   await _auditService.LogAsync(
       AuditActions.CreateInvoice,
       entityType: "Invoice",
       entityId: result.DocEntry.ToString(),
       details: $"Invoice #{result.DocNum} created for {request.CardCode}",
       isSuccess: true);
   ```

7. Wrap audit calls in try/catch — if audit logging fails, log the failure as a warning and ensure the main operation completes without interruption.

## Constraints

- Follow existing code conventions: interface + implementation, DI registration, NoTracking for reads.
- Use `[RequirePermission("audit.view")]` on any new audit retrieval endpoints.
- Do not modify the existing `AuditLog` entity — it already has all needed columns.
