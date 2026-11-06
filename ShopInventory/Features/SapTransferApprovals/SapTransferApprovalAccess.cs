using ErrorOr;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.SapTransferApprovals;

public sealed class SapTransferApprovalAccess(
    ApplicationDbContext context,
    ITransferWarehouseAuthorizer warehouseAuthorizer) : ISapTransferApprovalAccess
{
    /// <summary>
    /// The roles <see cref="ITransferWarehouseAuthorizer.EnsureCanConvertRequestAsync"/> lets convert a
    /// transfer request. Adding an approved draft moves stock out of its source warehouse exactly as a
    /// conversion does. <c>SapTransferApprovalQueueTests</c> holds the two lists together.
    /// </summary>
    internal static readonly string[] Roles =
    [
        ApplicationRoles.Admin,
        ApplicationRoles.StockController,
        ApplicationRoles.WashBay,
        ApplicationRoles.DepotController,
        ApplicationRoles.ShopStock
    ];

    public async Task<ErrorOr<SapTransferApprovalCaller>> ResolveAsync(Guid? userId, CancellationToken cancellationToken)
    {
        if (userId is not Guid id)
        {
            return SapTransferApprovalCaller.Unrestricted;
        }

        var role = await context.Users.AsNoTracking()
            .Where(user => user.Id == id && user.IsActive)
            .Select(user => user.Role)
            .FirstOrDefaultAsync(cancellationToken);
        if (role is null || !Roles.Contains(role, StringComparer.OrdinalIgnoreCase))
        {
            return Errors.SapTransferApproval.RoleNotAllowed;
        }

        // Null for the unrestricted roles; a depot controller's or a shop stock user's warehouses otherwise.
        var scope = await warehouseAuthorizer.GetSourceScopeAsync(id, cancellationToken);
        if (scope is null)
        {
            return SapTransferApprovalCaller.Unrestricted;
        }

        return scope.Count == 0
            ? Errors.SapTransferApproval.NoAssignedWarehouses
            : new SapTransferApprovalCaller(scope);
    }
}
