using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Commands.FetchDailyStock;

/// <summary>
/// Manual trigger to fetch daily stock snapshot from SAP for all configured warehouses.
/// </summary>
public sealed record FetchDailyStockCommand(
    DateTime? SnapshotDate = null,
    List<string>? Warehouses = null
) : IRequest<ErrorOr<FetchDailyStockResult>>;

public sealed record FetchDailyStockResult(
    DateTime SnapshotDate,
    int WarehouseCount,
    int TotalItemCount,
    List<WarehouseSnapshotResult> Warehouses
);

public sealed record WarehouseSnapshotResult(
    string WarehouseCode,
    int ItemCount,
    string Status
);
