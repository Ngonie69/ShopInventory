using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;

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
        var shop = user.Shop;

        if (shop is null && user.ShopId is not null)
        {
            shop = await context.Shops
                .AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Id == user.ShopId, cancellationToken);
        }

        // A shop-assigned account reports the shop's codes in the fields a client already reads, so
        // nothing has to learn about shops to get the right answer. The account's own columns are not
        // merged in: SellingAccountResolver prefers the shop outright, and a login payload that said
        // otherwise would have the till showing one warehouse and selling from another — the exact
        // split this whole change removes.
        var warehouseCodes = shop is not null
            ? [shop.WarehouseCode]
            : user.GetWarehouseCodes();

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
            ShopCode = shop?.Code,
            ShopName = shop?.Name
        };
    }
}
