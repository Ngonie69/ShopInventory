namespace ShopInventory.Web.Features.Reports.Queries.GetMerchandiserPurchaseOrderReport;

public sealed class GetMerchandiserPurchaseOrderReportResult
{
    public DateTime GeneratedAtUtc { get; init; }
    public DateTime? FromDate { get; init; }
    public DateTime? ToDate { get; init; }
    public Guid? MerchandiserUserId { get; init; }
    public bool? HasAttachments { get; init; }
    public string? Search { get; init; }
    public int TotalMerchandisers { get; init; }
    public int TotalOrders { get; init; }
    public int OrdersWithAttachments { get; init; }
    public int OrdersWithoutAttachments { get; init; }
    public int SyncedOrders { get; init; }
    public int UnsyncedOrders { get; init; }
    public int TotalAttachments { get; init; }
    public decimal TotalOrderValue { get; init; }
    public List<MerchandiserPurchaseOrderReportMerchandiserModel> Merchandisers { get; init; } = new();
    public List<MerchandiserPurchaseOrderReportOrderModel> Orders { get; init; } = new();
}