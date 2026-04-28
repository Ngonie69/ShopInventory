namespace ShopInventory.DTOs;

public sealed class MerchandiserPurchaseOrderReportLineDto
{
    public int LineNum { get; init; }
    public string ItemCode { get; init; } = string.Empty;
    public string? ItemDescription { get; init; }
    public decimal Quantity { get; init; }
    public decimal QuantityFulfilled { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal LineTotal { get; init; }
    public string? WarehouseCode { get; init; }
}