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
    /// role reads this at all.
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

        return Errors.Errors.DesktopSales.SalesReadNotPermitted;
    }
}
