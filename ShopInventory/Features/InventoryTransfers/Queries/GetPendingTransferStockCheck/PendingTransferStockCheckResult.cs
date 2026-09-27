namespace ShopInventory.Features.InventoryTransfers.Queries.GetPendingTransferStockCheck;

public sealed record PendingTransferStockCheckResult(
    Guid PendingTransferId,
    string FromWarehouse,
    string ToWarehouse,
    DateTime CheckedAt,
    bool StockWasFullyRead,
    List<string> UnreadableWarehouses,
    List<PendingTransferStockCheckLineResult> Lines)
{
    public int LinesInStock => Lines.Count(line => line.State == PendingTransferStockLineStates.InStock);

    public int LinesShort => Lines.Count(line => line.State == PendingTransferStockLineStates.Short);
}

/// <summary>
/// One line against the depot. <c>AvailableQuantity</c> is known only for a short line: the
/// validation reports the lines that fail, and a line that fits needs no figure to be posted.
/// </summary>
public sealed record PendingTransferStockCheckLineResult(
    int LineNumber,
    string ItemCode,
    string? UoMCode,
    string? BatchNumber,
    decimal Quantity,
    decimal? AvailableQuantity,
    string State);

public static class PendingTransferStockLineStates
{
    public const string InStock = "InStock";
    public const string Short = "Short";

    /// <summary>The line's warehouse did not answer, so nothing is known about it.</summary>
    public const string Unread = "Unread";
}
