using ErrorOr;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Models;

namespace ShopInventory.Services;

/// <summary>
/// Restricts who may action a transfer based on the warehouses assigned to their account.
/// Depot controllers and shops' stock users act on stock leaving a warehouse they run, so most
/// checks are against the transfer's source warehouse; deciding a held transfer is the exception
/// (<see cref="ITransferWarehouseAuthorizer.EnsureCanDecideAsync"/>).
/// </summary>
public interface ITransferWarehouseAuthorizer
{
    /// <summary>
    /// Confirms the user has a role that may convert transfer requests and, for a depot
    /// controller, that the request draws stock from one of their assigned warehouses.
    /// </summary>
    Task<ErrorOr<Success>> EnsureCanConvertRequestAsync(
        Guid userId,
        string? fromWarehouse,
        CancellationToken cancellationToken);

    /// <summary>
    /// Confirms the user may action a transfer that draws stock out of <paramref name="fromWarehouse"/>.
    /// </summary>
    Task<ErrorOr<Success>> EnsureCanActOnSourceAsync(Guid userId, string? fromWarehouse, CancellationToken cancellationToken);

    /// <summary>
    /// Confirms the user may put <paramref name="warehouseCode"/> onto a document. A warehouse-scoped
    /// user may only name warehouses assigned to them, whichever end of the transfer it sits on.
    /// </summary>
    /// <remarks>
    /// This is a hard refusal, not a route to approval: naming someone else's warehouse is the one
    /// thing a depot controller must never be able to do, so there is nothing to hold and review.
    /// </remarks>
    Task<ErrorOr<Success>> EnsureCanAssignWarehouseAsync(Guid userId, string? warehouseCode, CancellationToken cancellationToken);

    /// <summary>
    /// The source warehouses a user may action, or <c>null</c> when they are not warehouse-scoped.
    /// </summary>
    Task<IReadOnlyList<string>?> GetSourceScopeAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Confirms the user may decide, or read the stock check of, a held transfer between
    /// <paramref name="sourceWarehouses"/> and <paramref name="destinationWarehouses"/>: a
    /// warehouse-scoped user needs one of their warehouses at either end.
    /// </summary>
    /// <remarks>
    /// Either end, not the source alone, because deciding is the approval stage's question. A stage
    /// aimed at the receiving warehouse asks the shop's stock user to accept stock that leaves
    /// Dispatch, and a source-only check refused them the decision the stage offered. The stage still
    /// narrows it further: <see cref="InventoryTransferApprovalService.IsAuthorizer"/> decides which
    /// end counts, so this only keeps a scoped user off transfers touching none of their warehouses.
    /// </remarks>
    Task<ErrorOr<Success>> EnsureCanDecideAsync(
        Guid userId,
        IReadOnlyList<string> sourceWarehouses,
        IReadOnlyList<string> destinationWarehouses,
        CancellationToken cancellationToken);
}

public sealed class TransferWarehouseAuthorizer(ApplicationDbContext context) : ITransferWarehouseAuthorizer
{
    /// <summary>Roles whose transfer actions are limited to their assigned warehouses.</summary>
    private static readonly string[] ScopedRoles = [ApplicationRoles.DepotController, ApplicationRoles.ShopStock];

    public Task<ErrorOr<Success>> EnsureCanActOnSourceAsync(
        Guid userId,
        string? fromWarehouse,
        CancellationToken cancellationToken)
        => EnsureInScopeAsync(userId, fromWarehouse, Errors.InventoryTransfer.WarehouseNotAssigned, cancellationToken);

    public async Task<ErrorOr<Success>> EnsureCanConvertRequestAsync(
        Guid userId,
        string? fromWarehouse,
        CancellationToken cancellationToken)
    {
        var user = await context.Users.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == userId && item.IsActive, cancellationToken);
        if (user is null)
            return Errors.InventoryTransfer.ApproverNotAuthenticated;

        // The wash bay converts as a stock controller does: any source warehouse, no approval.
        if (string.Equals(user.Role, ApplicationRoles.Admin, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(user.Role, ApplicationRoles.StockController, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(user.Role, ApplicationRoles.WashBay, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Success;
        }

        // A depot controller or a shop's stock user converts only what leaves a warehouse they run.
        if (!ScopedRoles.Contains(user.Role, StringComparer.OrdinalIgnoreCase))
            return Errors.InventoryTransfer.TransferRequestConverterRoleRequired;

        return await EnsureInScopeAsync(
            userId, fromWarehouse, Errors.InventoryTransfer.WarehouseNotAssigned, cancellationToken);
    }

    public Task<ErrorOr<Success>> EnsureCanAssignWarehouseAsync(
        Guid userId,
        string? warehouseCode,
        CancellationToken cancellationToken)
        => EnsureInScopeAsync(userId, warehouseCode, Errors.InventoryTransfer.WarehouseNotAssignable, cancellationToken);

    public async Task<ErrorOr<Success>> EnsureCanDecideAsync(
        Guid userId,
        IReadOnlyList<string> sourceWarehouses,
        IReadOnlyList<string> destinationWarehouses,
        CancellationToken cancellationToken)
    {
        var scope = await GetSourceScopeAsync(userId, cancellationToken);
        if (scope is null)
            return Result.Success;

        if (scope.Count == 0)
            return Errors.InventoryTransfer.NoAssignedWarehouses;

        var touched = sourceWarehouses.Concat(destinationWarehouses)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .ToList();
        if (touched.Any(code => scope.Contains(code, StringComparer.OrdinalIgnoreCase)))
            return Result.Success;

        return Errors.InventoryTransfer.WarehouseNotAssigned(sourceWarehouses.FirstOrDefault() ?? string.Empty);
    }

    private async Task<ErrorOr<Success>> EnsureInScopeAsync(
        Guid userId,
        string? warehouseCode,
        Func<string, Error> outOfScope,
        CancellationToken cancellationToken)
    {
        var scope = await GetSourceScopeAsync(userId, cancellationToken);
        if (scope is null)
            return Result.Success;

        if (scope.Count == 0)
            return Errors.InventoryTransfer.NoAssignedWarehouses;

        if (string.IsNullOrWhiteSpace(warehouseCode))
            return Errors.InventoryTransfer.WarehouseCodeRequired;

        return scope.Contains(warehouseCode.Trim(), StringComparer.OrdinalIgnoreCase)
            ? Result.Success
            : outOfScope(warehouseCode);
    }

    public async Task<IReadOnlyList<string>?> GetSourceScopeAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await context.Users.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == userId && item.IsActive, cancellationToken);
        if (user is null)
            return Array.Empty<string>();

        // Administrators are deliberately unrestricted so a transfer is never left unactionable.
        if (string.Equals(user.Role, ApplicationRoles.Admin, StringComparison.OrdinalIgnoreCase))
            return null;

        if (!ScopedRoles.Contains(user.Role, StringComparer.OrdinalIgnoreCase))
            return null;

        return user.GetWarehouseCodes()
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
