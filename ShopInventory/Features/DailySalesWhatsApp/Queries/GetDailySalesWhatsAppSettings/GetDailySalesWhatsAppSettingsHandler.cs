using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using Keys = ShopInventory.Features.DailySalesWhatsApp.DailySalesWhatsAppKeys;

namespace ShopInventory.Features.DailySalesWhatsApp.Queries.GetDailySalesWhatsAppSettings;

public sealed class GetDailySalesWhatsAppSettingsHandler(ApplicationDbContext db)
    : IRequestHandler<GetDailySalesWhatsAppSettingsQuery, ErrorOr<DailySalesWhatsAppSettings>>
{
    public async Task<ErrorOr<DailySalesWhatsAppSettings>> Handle(
        GetDailySalesWhatsAppSettingsQuery request, CancellationToken cancellationToken)
    {
        var rows = await Keys.ReadAsync(db, cancellationToken);

        var warehouse = Keys.Text(rows, Keys.ShopWarehouseCode);
        var shopName = warehouse is null
            ? null
            : await db.Shops.AsNoTracking()
                .Where(shop => shop.WarehouseCode == warehouse)
                .OrderByDescending(shop => shop.IsActive)
                .Select(shop => shop.Name)
                .FirstOrDefaultAsync(cancellationToken);

        // When the settings were last saved, not when the report was last sent: the sent rows change daily.
        var saved = rows
            .Where(row => row.Key is not Keys.SentFor and not Keys.SentTo)
            .Select(row => (DateTime?)row.Value.UpdatedAt)
            .DefaultIfEmpty()
            .Max();

        return new DailySalesWhatsAppSettings(
            Keys.Flag(rows, Keys.Enabled),
            Keys.Numbers(Keys.Text(rows, Keys.Recipients)),
            warehouse,
            shopName,
            Keys.Text(rows, Keys.ShopHeading),
            Keys.DeserializeRows(Keys.Text(rows, Keys.VanRows)),
            Keys.DeserializeRows(Keys.Text(rows, Keys.ProductRows)),
            Keys.Text(rows, Keys.UpdatedBy),
            saved,
            Keys.Date(rows, Keys.SentFor),
            Keys.Numbers(Keys.Text(rows, Keys.SentTo)));
    }
}
