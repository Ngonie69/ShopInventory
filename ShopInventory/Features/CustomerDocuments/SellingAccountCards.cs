using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;

namespace ShopInventory.Features.CustomerDocuments;

/// <summary>
/// Whether a SAP card is an account something sells <i>as</i> rather than a customer.
/// </summary>
/// <remarks>
/// A till invoices its walk-ins to its shop's card, a van to its own, a cart vendor to its depot's.
/// Those cards carry every sale the till, van or vendor makes, for every buyer, so a WhatsApp number
/// saved on one would be sent all of their invoices. They are refused as owners of a number.
/// </remarks>
internal static class SellingAccountCards
{
    public static async Task<bool> IsSellingAccountAsync(
        ApplicationDbContext context,
        string cardCode,
        CancellationToken cancellationToken)
    {
        var code = cardCode.Trim();

        if (await context.Shops.AsNoTracking().AnyAsync(shop => shop.BusinessPartnerCode == code, cancellationToken))
            return true;

        return await context.Users
            .AsNoTracking()
            .AnyAsync(user => user.AssignedBusinessPartnerCode == code, cancellationToken);
    }
}
