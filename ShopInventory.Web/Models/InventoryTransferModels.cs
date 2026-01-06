namespace ShopInventory.Web.Models;

public class InventoryTransferDto
{
    public int DocEntry { get; set; }
    public int DocNum { get; set; }
    public string? DocDate { get; set; }
    public string? DueDate { get; set; }
    public string? FromWarehouse { get; set; }
    public string? ToWarehouse { get; set; }
    public string? Comments { get; set; }
    public List<InventoryTransferLineDto>? Lines { get; set; }
}

public class InventoryTransferLineDto
{
    public int LineNum { get; set; }
    public string? ItemCode { get; set; }
    public string? ItemDescription { get; set; }
    public decimal Quantity { get; set; }
    public string? FromWarehouseCode { get; set; }
    public string? ToWarehouseCode { get; set; }
    public string? UoMCode { get; set; }
}

public class InventoryTransferListResponse
{
    public string? Warehouse { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int Count { get; set; }
    public bool HasMore { get; set; }
    public List<InventoryTransferDto>? Transfers { get; set; }
}

public class InventoryTransferDateResponse
{
    public string? Warehouse { get; set; }
    public string? Date { get; set; }
    public string? FromDate { get; set; }
    public string? ToDate { get; set; }
    public int Count { get; set; }
    public List<InventoryTransferDto>? Transfers { get; set; }
}
