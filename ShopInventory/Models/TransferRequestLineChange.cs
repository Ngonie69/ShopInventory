namespace ShopInventory.Models;

/// <summary>
/// One line of an inventory transfer request as an edit leaves it. A kept line names its SAP
/// <see cref="LineNum"/>; a line being added has none and carries the item and warehouses instead.
/// </summary>
public sealed record TransferRequestLineChange(
    int? LineNum,
    string? ItemCode,
    decimal Quantity,
    string? FromWarehouseCode = null,
    string? ToWarehouseCode = null)
{
    public bool IsNew => LineNum is null;
}
