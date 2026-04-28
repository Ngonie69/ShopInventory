using ShopInventory.Models.Entities;

namespace ShopInventory.DTOs;

public sealed class MerchandiserPurchaseOrderReportOrderDto
{
    public int SalesOrderId { get; init; }
    public string OrderNumber { get; init; } = string.Empty;
    public string AttachmentReference { get; init; } = string.Empty;
    public DateTime OrderDateUtc { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public string CardCode { get; init; } = string.Empty;
    public string? CardName { get; init; }
    public string? CustomerRefNo { get; init; }
    public SalesOrderStatus Status { get; init; }
    public int? SapDocEntry { get; init; }
    public int? SapDocNum { get; init; }
    public bool IsSynced { get; init; }
    public string? WarehouseCode { get; init; }
    public decimal DocTotal { get; init; }
    public string? Currency { get; init; }
    public Guid MerchandiserUserId { get; init; }
    public string MerchandiserUsername { get; init; } = string.Empty;
    public string MerchandiserFullName { get; init; } = string.Empty;
    public string? MerchandiserNotes { get; init; }
    public int ItemCount { get; init; }
    public decimal TotalQuantity { get; init; }
    public bool HasAttachments { get; init; }
    public int AttachmentCount { get; init; }
    public List<MerchandiserPurchaseOrderReportAttachmentDto> Attachments { get; init; } = new();
    public List<MerchandiserPurchaseOrderReportLineDto> Lines { get; init; } = new();
}