namespace ShopInventory.Features.InventoryTransfers.Commands.AnnounceTransferArrivals;

/// <summary>One transfer's stock arriving in one till warehouse.</summary>
internal sealed record TransferArrival(
    int DocEntry,
    int DocNum,
    string WarehouseCode,
    string? SourceWarehouse,
    int ItemCount);
