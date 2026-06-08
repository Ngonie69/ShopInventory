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
           ApplicationRoles.UsesLegacyRouteCustomerScope(role);

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
}
