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
    /// The roles that may open the held transfers at all: the people who may convert a transfer request
    /// (<see cref="ITransferWarehouseAuthorizer.EnsureCanConvertRequestAsync"/>).
    /// <c>SapTransferApprovalQueueTests</c> holds the two lists together.
    /// </summary>
    internal static readonly string[] Roles =
    [
        ApplicationRoles.Admin,
        ApplicationRoles.StockController,
        ApplicationRoles.WashBay,
        ApplicationRoles.DepotController,
        ApplicationRoles.ShopStock
    ];

    /// <summary>
    /// The wash bay's warehouse. A WashBay account holds no warehouse assignments
    /// (<see cref="ApplicationRoles.SupportsWarehouseAssignments"/>), so its one warehouse is this.
    /// </summary>
    internal const string WashBayWarehouse = "WASHBAY";

    /// <summary>
    /// The roles that see every warehouse's held transfers but approve and add only those leaving or
    /// entering their own warehouses. The user, 2026-11-23: "washbay and shopstock should be able to view
    /// SAP held transfers, approve drafts and add, for the warehouse they are linked to"; and 2026-11-24:
    /// "shopstock and washbay should be able to see all warehouse under sap held transfers". What they
    /// approve and add is mostly coming in — into WASHBAY, into the factory shop from Dispatch — which a
    /// source-only scope never allowed them.
    /// </summary>
    internal static readonly string[] EitherEndRoles = [ApplicationRoles.WashBay, ApplicationRoles.ShopStock];

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

        if (string.Equals(role, ApplicationRoles.WashBay, StringComparison.OrdinalIgnoreCase))
        {
            return new SapTransferApprovalCaller([WashBayWarehouse], EitherEnd: true);
        }

        // Null for the unrestricted roles; a depot controller's or a shop stock user's warehouses otherwise.
        var scope = await warehouseAuthorizer.GetSourceScopeAsync(id, cancellationToken);
        if (scope is null)
        {
            return SapTransferApprovalCaller.Unrestricted;
        }

        return scope.Count == 0
            ? Errors.SapTransferApproval.NoAssignedWarehouses
            : new SapTransferApprovalCaller(scope, EitherEnd: EitherEndRoles.Contains(role, StringComparer.OrdinalIgnoreCase));
    }
}
