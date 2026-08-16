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
}
