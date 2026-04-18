using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Commands.ProcessTransferEvent;

/// <summary>
/// Processes a transfer event received from TransferEventListener.
/// Adjusts daily stock snapshot quantities accordingly.
/// </summary>
public sealed record ProcessTransferEventCommand(
    string ItemCode,
    string SourceWarehouse,
    string DestinationWarehouse,
    decimal Quantity,
    int? SapDocEntry,
    int? SapDocNum,
    string? ItemDescription = null
) : IRequest<ErrorOr<ProcessTransferEventResult>>;

public sealed record ProcessTransferEventResult(
    bool Adjusted,
    string Message,
    List<StockAdjustmentDetail> Adjustments
);

public sealed record StockAdjustmentDetail(
    string WarehouseCode,
    string Direction,
    decimal AdjustmentQuantity,
    decimal NewAvailableQuantity
);
