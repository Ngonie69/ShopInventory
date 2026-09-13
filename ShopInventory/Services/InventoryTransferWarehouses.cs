using ShopInventory.Models;

namespace ShopInventory.Services;

/// <summary>
/// Which warehouses a stock transfer, and each of its lines, actually moves stock between.
/// </summary>
/// <remarks>
/// A line's own warehouses win and the header is the fallback, the order TransferEventListener uses
/// (<c>line.FromWarehouseCode ?? transfer.FromWarehouse</c>, <c>line.WarehouseCode ?? transfer.ToWarehouse</c>)
/// when it adjusts a shop's stock ledger. Reading them any other way lets the ledger and the transfer
/// list disagree about the same document.
/// </remarks>
public static class InventoryTransferWarehouses
{
    public static string From(InventoryTransfer transfer, InventoryTransferLine line) =>
        FirstNonBlank(line.FromWarehouseCode, transfer.FromWarehouse);

    public static string To(InventoryTransfer transfer, InventoryTransferLine line) =>
        FirstNonBlank(line.WarehouseCode, transfer.ToWarehouse);

    /// <summary>Whether the transfer moves any stock into or out of the warehouse.</summary>
    public static bool Touches(InventoryTransfer transfer, string warehouseCode)
    {
        if (Is(transfer.FromWarehouse, warehouseCode) || Is(transfer.ToWarehouse, warehouseCode))
        {
            return true;
        }

        return transfer.StockTransferLines?.Any(line =>
            Is(From(transfer, line), warehouseCode) || Is(To(transfer, line), warehouseCode)) == true;
    }

    private static string FirstNonBlank(string? line, string? header) =>
        (string.IsNullOrWhiteSpace(line) ? header : line)?.Trim() ?? string.Empty;

    private static bool Is(string? code, string warehouseCode) =>
        string.Equals(code?.Trim(), warehouseCode.Trim(), StringComparison.OrdinalIgnoreCase);
}
