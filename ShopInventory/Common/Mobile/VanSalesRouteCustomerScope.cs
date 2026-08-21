using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Common.Mobile;

public static class VanSalesRouteCustomerScope
{
    public static bool UsesLocalRouteCustomers(User user)
        => UsesLocalRouteCustomers(user.Role, user.AssignedBusinessPartnerCode);

    public static bool UsesLocalRouteCustomers(string? role, string? assignedBusinessPartnerCode)
        => !string.IsNullOrWhiteSpace(assignedBusinessPartnerCode) &&
           ApplicationRoles.UsesRouteCustomerScope(role);

    public static string? GetAssignedBusinessPartnerCode(User user)
        => UsesLocalRouteCustomers(user)
            ? user.AssignedBusinessPartnerCode?.Trim()
            : null;

    public static async Task<List<RouteCustomerEntity>> GetAssignedRouteCustomersAsync(
        ApplicationDbContext db,
        User user,
        CancellationToken cancellationToken)
    {
        var assignedBusinessPartnerCode = GetAssignedBusinessPartnerCode(user);
        if (string.IsNullOrWhiteSpace(assignedBusinessPartnerCode))
        {
            return [];
        }

        return await db.RouteCustomers
            .AsNoTracking()
            .Where(customer => customer.AssignedBusinessPartnerCode == assignedBusinessPartnerCode && customer.IsActive)
            .OrderBy(customer => customer.Name)
            .ThenBy(customer => customer.Code)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Finds one customer a business partner may invoice, by code, or null if it may not.
    /// </summary>
    /// <remarks>
    /// Deliberately next to <see cref="GetAssignedRouteCustomersAsync"/> and filtered the same way.
    /// The list is what an operator picks from and this is what the server accepts, so if the two
    /// ever disagreed a deactivated customer would keep trading for anyone holding a stale list.
    /// </remarks>
    public static async Task<RouteCustomerEntity?> FindAssignableAsync(
        ApplicationDbContext db,
        string? assignedBusinessPartnerCode,
        string? code,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(assignedBusinessPartnerCode) || string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var trimmedCode = code.Trim();
        var trimmedBusinessPartner = assignedBusinessPartnerCode.Trim();

        return await db.RouteCustomers
            .AsNoTracking()
            .FirstOrDefaultAsync(
                customer => customer.AssignedBusinessPartnerCode == trimmedBusinessPartner
                    && customer.IsActive
                    && customer.Code == trimmedCode,
                cancellationToken);
    }

    /// <summary>
    /// Finds one customer on a business partner's route by code, whether or not it is still active,
    /// or null if the route has never had it.
    /// </summary>
    /// <remarks>
    /// The counterpart to <see cref="FindAssignableAsync"/>, and deliberately beside it so the one
    /// difference between them is visible: that one answers "may this be traded with", so it must
    /// exclude a removed customer; this one answers "which row does this code name on this route",
    /// which a removal does not change.
    ///
    /// Managing a customer needs the second question. A handset with no signal records the removal
    /// and posts it when it can, so the same removal arrives more than once as a matter of course —
    /// a rep who deletes a shop, drives out of range and comes back has done nothing unusual. Asking
    /// the trading question there would resolve the first attempt and fail every replay after it as
    /// though the shop had never existed, which leaves the queue holding a delete it can never land.
    /// Resolving the row regardless lets the caller see it is already removed and say so.
    ///
    /// It is still scoped to the route. A code is only unique within one, and no rep may reach
    /// another route's customer by naming it.
    /// </remarks>
    public static async Task<RouteCustomerEntity?> FindOnRouteAsync(
        ApplicationDbContext db,
        string? assignedBusinessPartnerCode,
        string? code,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(assignedBusinessPartnerCode) || string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var trimmedCode = code.Trim();
        var trimmedBusinessPartner = assignedBusinessPartnerCode.Trim();

        return await db.RouteCustomers
            .AsNoTracking()
            .FirstOrDefaultAsync(
                customer => customer.AssignedBusinessPartnerCode == trimmedBusinessPartner
                    && customer.Code == trimmedCode,
                cancellationToken);
    }
}
