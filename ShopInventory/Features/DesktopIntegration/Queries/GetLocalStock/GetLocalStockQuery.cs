using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetLocalStock;

/// <summary>
/// Retrieves available stock from today's local snapshot for a warehouse.
/// </summary>
public sealed record GetLocalStockQuery(
    string WarehouseCode,
    DateTime? SnapshotDate = null
) : IRequest<ErrorOr<LocalStockResult>>;

public sealed record LocalStockResult(
    string WarehouseCode,
    DateTime SnapshotDate,
    string SnapshotStatus,
    List<LocalStockItemDto> Items,
    // Why a Failed snapshot failed, or what a Complete one is short of. A bare "Failed" badge left the
    // operator nothing to act on, and the reason was only ever in the API log.
    string? LastError = null
);

public sealed record LocalStockItemDto(
    string ItemCode,
    string? ItemDescription,
    string WarehouseCode,
    decimal AvailableQuantity,
    decimal OriginalQuantity,
    decimal TransferAdjustment,
    List<LocalStockBatchDto> Batches
);

public sealed record LocalStockBatchDto(
    string? BatchNumber,
    decimal AvailableQuantity,
    decimal OriginalQuantity,
    DateTime? ExpiryDate
);
