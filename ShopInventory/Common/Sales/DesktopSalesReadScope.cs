using ErrorOr;
using ShopInventory.Models;

namespace ShopInventory.Common.Sales;

/// <summary>
/// Which sales a caller may read.
/// </summary>
/// <param name="WarehouseCode">
/// The one warehouse the caller is confined to, or null when they may read every shop's takings.
/// </param>
public sealed record DesktopSalesReadScope(string? WarehouseCode)
{
    /// <summary>Reads across every shop.</summary>
    public static readonly DesktopSalesReadScope Unrestricted = new((string?)null);

    public bool IsUnrestricted => WarehouseCode is null;

    /// <summary>
    /// The scope one read runs under, given the warehouse the caller asked for.
    /// </summary>
    /// <remarks>
    /// Refused rather than narrowed when a confined caller names another warehouse: a page headed with
    /// one warehouse and filled with another's takings is worse than an error, and silently rewriting
    /// the request would hide a client bug — or a probe — that somebody should see. A confined caller
    /// who names none is narrowed to their own shop, never widened to every shop. For anyone entitled to
    /// the whole set the parameter stays a filter.
    ///
    /// Shared by every read of till takings so that the list and the analysis cannot come to disagree
    /// about whose money a caller may see.
    /// </remarks>
    public ErrorOr<DesktopSalesReadScope> Narrow(string? requestedWarehouse)
    {
        var requested = string.IsNullOrWhiteSpace(requestedWarehouse) ? null : requestedWarehouse.Trim();

        if (IsUnrestricted)
        {
            return requested is null ? this : new DesktopSalesReadScope(requested);
        }

        if (requested is not null && !string.Equals(requested, WarehouseCode, StringComparison.OrdinalIgnoreCase))
        {
            return Errors.Errors.DesktopSales.SalesReadOutsideScope(requested, WarehouseCode!);
        }

        return this;
    }

    /// <summary>
    /// The warehouses one read may actually touch, given the set the caller asked for.
    /// </summary>
    /// <remarks>
    /// The many-warehouse form of <see cref="Narrow"/>, for a console whose warehouse control is a row of
    /// chips rather than one picker. It refuses on exactly the same rule — a confined caller naming any
    /// warehouse that is not theirs is an error, not a set to quietly intersect — because silently
    /// dropping the warehouses they may not read would answer a five-shop question with one shop's
    /// takings and say nothing about the four it left out.
    ///
    /// An empty ask is every warehouse in scope: null for an unrestricted caller, and the confined
    /// caller's own for anyone else. Returning their own warehouse rather than null is what keeps the
    /// scope on the query when the console names nothing.
    /// </remarks>
    public ErrorOr<List<string>> NarrowMany(IReadOnlyList<string>? requestedWarehouses)
    {
        var requested = (requestedWarehouses ?? [])
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (IsUnrestricted)
        {
            return requested;
        }

        var outside = requested.FirstOrDefault(
            code => !string.Equals(code, WarehouseCode, StringComparison.OrdinalIgnoreCase));

        if (outside is not null)
        {
            return Errors.Errors.DesktopSales.SalesReadOutsideScope(outside, WarehouseCode!);
        }

        // Their own warehouse whether they named it or not, so an unnamed ask is still scoped.
        return new List<string> { WarehouseCode! };
    }
}

/// <summary>
/// Resolves what a caller is allowed to read, as the counterpart to
/// <see cref="SellingAccountResolver"/>.
///
/// <c>GET /api/DesktopIntegration/sales</c> took its warehouse straight off the query string and
/// checked it against nobody. The only guard was the class-level "ApiAccess" policy, which admits
/// every staff role — so a merchandiser, a driver or a van rep could read any shop's takings, with
/// every money column, by editing one parameter. The warehouse is decided here now, from the account,
/// and a request naming a different one is refused rather than quietly rescoped.
/// </summary>
public static class DesktopSalesReadScopeResolver
{
    /// <summary>
    /// Roles that read every shop's takings even when they are assigned to one.
    /// </summary>
    /// <remarks>
    /// An administrator assigned to a counter is still an administrator, and an integration account
    /// feeds the consoles rather than working a till.
    /// </remarks>
    private static readonly string[] AlwaysUnrestrictedRoles =
    [
        ApplicationRoles.Admin,
        ApplicationRoles.ApiUser
    ];

    /// <summary>
    /// Roles that read across every shop when they are not assigned to one.
    /// </summary>
    /// <remarks>
    /// Deliberately much narrower than the "ApiAccess" role list this endpoint used to rely on. The
    /// three consumers are the two web pages — <c>/desktop-sales</c> is Admin and Cashier,
    /// <c>/desktop-transactions</c> is Admin — and the till itself, which is shop-scoped. No handset
    /// role reads this at all; a cart vendor reads it confined to its one warehouse, further down.
    ///
    /// Cashier is here and also shop-scopable, which is the case that matters: the accounts working
    /// tills today hold this role, and pointing one at a shop is what confines it.
    /// </remarks>
    private static readonly string[] UnrestrictedWithoutShopRoles =
    [
        ApplicationRoles.Cashier,
        ApplicationRoles.Manager
    ];

    /// <summary>
    /// Reads the caller's scope off the account, or refuses.
    /// </summary>
    /// <remarks>
    /// Keyed on the assigned shop rather than on the role, so that an existing till account still
    /// holding <see cref="ApplicationRoles.Cashier"/> is scoped the moment it is pointed at a shop —
    /// which is what the backfill does. Were this keyed on <see cref="ApplicationRoles.TillOperator"/>
    /// alone, every backfilled till would keep reading every shop's money until somebody remembered to
    /// change its role too.
    ///
    /// Administrators and integration accounts are unrestricted even when they carry a shop, because
    /// an administrator assigned to a counter is still an administrator.
    ///
    /// Callers must have loaded <see cref="User.Shop"/>; a <see cref="User.ShopId"/> set with no shop
    /// attached is a fault in the query rather than a reason to widen the scope.
    /// </remarks>
    public static ErrorOr<DesktopSalesReadScope> Resolve(User? user)
    {
        if (user is null || !user.IsActive)
        {
            return Errors.Errors.DesktopSales.Unauthenticated;
        }

        if (AlwaysUnrestrictedRoles.Contains(user.Role, StringComparer.OrdinalIgnoreCase))
        {
            return DesktopSalesReadScope.Unrestricted;
        }

        if (user.ShopId is not null)
        {
            if (user.Shop is null)
            {
                throw new InvalidOperationException(
                    $"User {user.Id} has ShopId {user.ShopId} but Shop was not loaded. " +
                    "Include(u => u.Shop) before resolving a desktop sales read scope.");
            }

            var warehouseCode = user.Shop.WarehouseCode?.Trim();

            // A shop with no warehouse cannot scope anything. Widening to unrestricted here would turn
            // a misconfigured shop into a way to read every other shop's takings.
            return string.IsNullOrWhiteSpace(warehouseCode)
                ? Errors.Errors.DesktopSales.ShopMisconfigured(
                    string.IsNullOrWhiteSpace(user.Shop.Name) ? user.Shop.Code : user.Shop.Name,
                    "warehouse")
                : new DesktopSalesReadScope(warehouseCode);
        }

        if (UnrestrictedWithoutShopRoles.Contains(user.Role, StringComparer.OrdinalIgnoreCase))
        {
            return DesktopSalesReadScope.Unrestricted;
        }

        // A cart vendor has no shop — the admin handlers refuse it one — but it does sell out of exactly
        // one warehouse, and it is confined to that warehouse's takings the way a till is to its shop's.
        // Without this its dashboard, stock ledger and sales-and-stock screens all stopped at a 403: the
        // stock ledger cannot explain a balance without the sales that moved it. The warehouse comes from
        // the same resolver the sale is made on, and an account with none or several is refused rather
        // than widened.
        if (string.Equals(user.Role, ApplicationRoles.CartVendor, StringComparison.OrdinalIgnoreCase))
        {
            var warehouseCode = SellingAccountResolver.ResolveAssignedWarehouse(user);
            return warehouseCode.IsError
                ? warehouseCode.Errors
                : new DesktopSalesReadScope(warehouseCode.Value);
        }

        return Errors.Errors.DesktopSales.SalesReadNotPermitted;
    }
}
