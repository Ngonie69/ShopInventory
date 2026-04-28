namespace ShopInventory.Web.Features.Reports.Queries.GetMerchandiserPurchaseOrderReport;

public sealed class MerchandiserPurchaseOrderReportOrderModel
{
    public int SalesOrderId { get; init; }
    public string OrderNumber { get; init; } = string.Empty;
    public string AttachmentReference { get; init; } = string.Empty;
    public DateTime OrderDateUtc { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public string CardCode { get; init; } = string.Empty;
    public string? CardName { get; init; }
    public string? CustomerRefNo { get; init; }
    public int Status { get; init; }
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
    public List<MerchandiserPurchaseOrderReportAttachmentModel> Attachments { get; init; } = new();
    public List<MerchandiserPurchaseOrderReportLineModel> Lines { get; init; } = new();

    public string StatusLabel => Status switch
    {
        0 => "Draft",
        1 => "Pending",
        2 => "Approved",
        3 => "Partially Fulfilled",
        4 => "Fulfilled",
        5 => "Cancelled",
        6 => "On Hold",
        _ => "Unknown"
    };
}