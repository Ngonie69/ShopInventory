namespace ShopInventory.Features.InventoryTransfers.Queries.GetTransferRequestStock;

public sealed record TransferRequestStockResult(
    int DocEntry,
    int DocNum,
    DateTime CheckedAt,
    bool StockWasFullyRead,
    List<string> UnreadableWarehouses,
    List<TransferRequestStockLineResult> Lines,
    List<TransferRequestItemStockResult> Items)
{
    public int LinesShort => Lines.Count(line => line.State == TransferRequestStockLineStates.Short);
}

/// <summary>
/// One request line against its source warehouse. A line is short when everything the request
/// asks of that item in that warehouse, over all its lines, is more than is available.
/// </summary>
public sealed record TransferRequestStockLineResult(
    int LineNum,
    string ItemCode,
    string WarehouseCode,
    decimal Quantity,
    decimal? Available,
    string State);

/// <summary>
/// One item in one warehouse. <c>Available</c> is SAP's In Stock less what the tills have sold
/// there that SAP has not been told about yet, never below zero; null when the warehouse did not
/// answer.
/// </summary>
public sealed record TransferRequestItemStockResult(
    string WarehouseCode,
    string ItemCode,
    decimal? InStock,
    decimal HeldByTills,
    decimal? Available);

public static class TransferRequestStockLineStates
{
    public const string InStock = "InStock";
    public const string Short = "Short";

    /// <summary>The line's warehouse did not answer, so nothing is known about it.</summary>
    public const string Unread = "Unread";
}
