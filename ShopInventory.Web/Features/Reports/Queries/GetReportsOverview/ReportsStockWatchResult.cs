using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.Reports.Queries.GetReportsOverview;

/// <summary>The stock the hub flags: lines under their minimum, and items that have stopped selling.</summary>
public sealed record ReportsStockWatchResult(
    LowStockAlertReport? LowStock,
    SlowMovingProductsReport? SlowMoving,
    IReadOnlyCollection<string> Unavailable);
