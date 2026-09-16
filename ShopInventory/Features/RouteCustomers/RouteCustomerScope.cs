using ShopInventory.Data;
using ShopInventory.Features.Vending;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.RouteCustomers;

/// <summary>
/// Which of the two populations in the route customer table a read is asking for.
/// </summary>
/// <remarks>
/// A van route is a round a van drives, and its route customers are the shops on that round. A vending
/// vendor sells from a cart out of a depot and is on no round at all — it has a depot, not a route. The
/// two share one table for historical reasons, and nothing on a row says which it is: a vendor is a row
/// under a business partner an <see cref="ApplicationRoles.CartVendor"/> account sells on, the same
/// reading <c>GetVendingOverviewHandler</c> makes.
///
/// Sharing the table is not a reason to share the word. <see cref="Route"/> is the default because
/// "route customer" is what the endpoint, the pages and the reports all say, and a caller who says
/// nothing is asking about routes. Vending asks for its own half by name.
/// </remarks>
public enum RouteCustomerScope
{
    /// <summary>The van routes' shops alone — customers with a route. The default.</summary>
    Route = 0,

    /// <summary>The vending depots' vendors alone — customers with a depot.</summary>
    Vending = 1,

    /// <summary>Both, for a read that is about the table rather than about either operation.</summary>
    All = 2,
}

/// <summary>
/// The one place the split above is decided, so no two reads can disagree about which rows are vendors.
/// </summary>
public static class RouteCustomerScopes
{
    /// <summary>
    /// The business partner codes vending sells on. Empty for <see cref="RouteCustomerScope.All"/>,
    /// which needs no split and should not pay for the user read.
    /// </summary>
    public static async Task<HashSet<string>> VendingDepotCodesAsync(
        ApplicationDbContext context,
        RouteCustomerScope scope,
        CancellationToken cancellationToken)
    {
        if (scope == RouteCustomerScope.All)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var depots = await VendingDepots.LoadAsync(context, cancellationToken);
        return depots
            .Select(depot => depot.BusinessPartnerCode)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Whether a row assigned to this business partner belongs in the scope.</summary>
    public static bool Includes(
        RouteCustomerScope scope,
        IReadOnlySet<string> vendingDepotCodes,
        string? assignedBusinessPartnerCode) => scope switch
        {
            RouteCustomerScope.All => true,
            RouteCustomerScope.Vending => assignedBusinessPartnerCode is not null
                && vendingDepotCodes.Contains(assignedBusinessPartnerCode),
            _ => assignedBusinessPartnerCode is null
                || !vendingDepotCodes.Contains(assignedBusinessPartnerCode),
        };

    /// <summary>
    /// The same decision as <see cref="Includes"/>, pushed into the database. The comparison runs under
    /// the column's own collation rather than <see cref="StringComparer.OrdinalIgnoreCase"/>; both codes
    /// are written from the same user assignment, so they agree on spelling.
    /// </summary>
    public static IQueryable<RouteCustomerEntity> Apply(
        IQueryable<RouteCustomerEntity> customers,
        RouteCustomerScope scope,
        IReadOnlyCollection<string> vendingDepotCodes)
    {
        if (scope == RouteCustomerScope.All || vendingDepotCodes.Count == 0)
        {
            // With no depots configured there are no vendors, so a vending read is empty and a route
            // read is the whole table. Saying so here keeps EF from emitting `IN ()`.
            return scope == RouteCustomerScope.Vending && vendingDepotCodes.Count == 0
                ? customers.Where(_ => false)
                : customers;
        }

        var codes = vendingDepotCodes.ToList();

        return scope == RouteCustomerScope.Vending
            ? customers.Where(customer => codes.Contains(customer.AssignedBusinessPartnerCode))
            : customers.Where(customer => !codes.Contains(customer.AssignedBusinessPartnerCode));
    }
}
