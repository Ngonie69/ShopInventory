using ShopInventory.Common.Sap;
using ShopInventory.Configuration;
using ShopInventory.Data;

namespace ShopInventory.Services;

/// <summary>
/// The first trading day the van posting pass reaches back to, once SAP outages are counted.
/// </summary>
/// <remarks>
/// <see cref="VanSalesPostingSettings.WindowStart"/> widened by <see cref="SapOutageReach"/>. One
/// method because two places must agree on it: the pass that posts, and the Exception Center, which
/// calls a sale older than this stranded.
/// </remarks>
public static class VanSalesPostingWindow
{
    public static Task<DateTime> StartAsync(
        ApplicationDbContext db,
        VanSalesPostingSettings posting,
        SapAvailabilitySettings availability,
        DateTime tradingDate,
        CancellationToken cancellationToken) =>
        SapOutageReach.ExtendAsync(
            db,
            posting.WindowStart(tradingDate),
            availability.MaxLookbackExtensionDays,
            DateTime.UtcNow,
            cancellationToken);
}
