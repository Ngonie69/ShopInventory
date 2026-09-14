using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Common.Auth;

/// <summary>
/// The one place a <see cref="User"/> becomes the <see cref="UserInfo"/> a client is told about itself.
/// </summary>
/// <remarks>
/// There were three hand-written copies of this — two in <c>AuthService</c> and one in
/// <c>GetCurrentUserHandler</c> — and they had already drifted: the current-user one omitted the
/// business partner and the cost centre that the other two sent, so an account's own profile
/// disagreed with what it was handed at sign-in. Adding the shop to three copies would have made
/// four ways to be wrong.
///
/// The shop is resolved here rather than demanded of the caller because the five paths that build
/// this reach their user differently, and two of them use <c>FindAsync</c>, which cannot
/// <c>Include</c> at all. A caller that already loaded the shop pays nothing; one that did not gets
/// a single keyed lookup, and only when the account actually has a shop.
/// </remarks>
public static class UserInfoMapper
{
    public static async Task<UserInfo> FromUserAsync(
        User user,
        ApplicationDbContext context,
        CancellationToken cancellationToken = default)
    {
        var shop = await LoadShopAsync(user, context, cancellationToken);
        var warehouseCodes = WarehouseCodes(user, shop);

        return new UserInfo
        {
            Username = user.Username,
            Role = user.Role,
            Email = user.Email,
            AssignedWarehouseCode = shop?.WarehouseCode ?? user.AssignedWarehouseCode,
            AssignedWarehouseCodes = warehouseCodes,
            AssignedSection = user.AssignedSection,
            AssignedBusinessPartnerCode = shop?.BusinessPartnerCode ?? user.AssignedBusinessPartnerCode,
            AssignedCostCentreCode = shop?.CostCentreCode ?? user.AssignedCostCentreCode,
            AssignedCustomerCodes = user.GetCustomerCodes(),
            // Not merged either: an account on a shop whose supplying warehouse is unset is told so,
            // rather than handed an account-level value the request handler would then refuse.
            TransferSourceWarehouseCode = shop is not null
                ? shop.SupplyingWarehouseCode
                : user.SupplyingWarehouseCode,
            ShopCode = shop?.Code,
            ShopName = shop?.Name
        };
    }

    /// <summary>
    /// The warehouses an account works in: its shop's, when it has a shop, otherwise its own.
    /// </summary>
    /// <remarks>
    /// Shared with the access token, which writes one <c>warehouse</c> claim for each. The token used
    /// to read the account's own column alone, and a till operator never has anything in it — the
    /// role takes its warehouse from the shop, and user management refuses warehouse assignments for
    /// it. So a till's token carried no warehouse, <c>NotificationHub</c> put its connection in no
    /// warehouse group, and <c>InvoiceCancelled</c>, which is sent only to those groups, could not
    /// reach the tills it was written for.
    /// </remarks>
    public static async Task<List<string>> ResolveWarehouseCodesAsync(
        User user,
        ApplicationDbContext context,
        CancellationToken cancellationToken = default) =>
        WarehouseCodes(user, await LoadShopAsync(user, context, cancellationToken));

    private static async Task<ShopEntity?> LoadShopAsync(
        User user,
        ApplicationDbContext context,
        CancellationToken cancellationToken)
    {
        if (user.Shop is not null || user.ShopId is null)
        {
            return user.Shop;
        }

        return await context.Shops
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == user.ShopId, cancellationToken);
    }

    // A shop-assigned account reports the shop's codes in the fields a client already reads, so
    // nothing has to learn about shops to get the right answer. The account's own columns are not
    // merged in: SellingAccountResolver prefers the shop outright, and a login payload that said
    // otherwise would have the till showing one warehouse and selling from another — the exact
    // split this whole change removes.
    private static List<string> WarehouseCodes(User user, ShopEntity? shop) =>
        shop is not null
            ? [shop.WarehouseCode]
            : user.GetWarehouseCodes();
}
