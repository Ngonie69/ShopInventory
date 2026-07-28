using ErrorOr;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Models;

namespace ShopInventory.Services;

/// <summary>
/// Restricts who may action a transfer based on the warehouses assigned to their account.
/// Depot controllers only ever act on stock leaving a warehouse they run, so the check is
/// against the transfer's source warehouse.
/// </summary>
public interface ITransferWarehouseAuthorizer
{
    /// <summary>
    /// Confirms the user may action a transfer that draws stock out of <paramref name="fromWarehouse"/>.
    /// </summary>
    Task<ErrorOr<Success>> EnsureCanActOnSourceAsync(Guid userId, string? fromWarehouse, CancellationToken cancellationToken);

    /// <summary>
    /// The source warehouses a user may action, or <c>null</c> when they are not warehouse-scoped.
    /// </summary>
    Task<IReadOnlyList<string>?> GetSourceScopeAsync(Guid userId, CancellationToken cancellationToken);
}

public sealed class TransferWarehouseAuthorizer(ApplicationDbContext context) : ITransferWarehouseAuthorizer
{
    /// <summary>Roles whose transfer actions are limited to their assigned warehouses.</summary>
    private static readonly string[] ScopedRoles = [ApplicationRoles.DepotController];

    public async Task<ErrorOr<Success>> EnsureCanActOnSourceAsync(
        Guid userId,
        string? fromWarehouse,
        CancellationToken cancellationToken)
    {
        var scope = await GetSourceScopeAsync(userId, cancellationToken);
        if (scope is null)
            return Result.Success;

        if (scope.Count == 0)
            return Errors.InventoryTransfer.NoAssignedWarehouses;

        if (string.IsNullOrWhiteSpace(fromWarehouse))
            return Errors.InventoryTransfer.WarehouseCodeRequired;

        return scope.Contains(fromWarehouse.Trim(), StringComparer.OrdinalIgnoreCase)
            ? Result.Success
            : Errors.InventoryTransfer.WarehouseNotAssigned(fromWarehouse);
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
