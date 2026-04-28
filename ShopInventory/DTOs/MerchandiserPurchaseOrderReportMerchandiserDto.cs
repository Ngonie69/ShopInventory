namespace ShopInventory.DTOs;

public sealed class MerchandiserPurchaseOrderReportMerchandiserDto
{
    public Guid MerchandiserUserId { get; init; }
    public string Username { get; init; } = string.Empty;
    public string FullName { get; init; } = string.Empty;
    public int OrderCount { get; init; }
    public int OrdersWithAttachments { get; init; }
    public int AttachmentCount { get; init; }
    public int SyncedOrders { get; init; }
    public decimal TotalOrderValue { get; init; }
    public DateTime? LatestOrderCreatedAtUtc { get; init; }
}