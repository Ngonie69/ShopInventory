namespace ShopInventory.DTOs;

/// <summary>
/// DTO for inventory transfer response
/// </summary>
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

/// <summary>
/// DTO for inventory transfer line
/// </summary>
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

/// <summary>
/// DTO for paginated inventory transfer response
/// </summary>
public class InventoryTransferListResponseDto
{
    public string? Warehouse { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int Count { get; set; }
    public bool HasMore { get; set; }
    public List<InventoryTransferDto>? Transfers { get; set; }
}

/// <summary>
/// DTO for inventory transfer response by date
/// </summary>
public class InventoryTransferDateResponseDto
{
    public string? Warehouse { get; set; }
    public string? Date { get; set; }
    public string? FromDate { get; set; }
    public string? ToDate { get; set; }
    public int Count { get; set; }
    public List<InventoryTransferDto>? Transfers { get; set; }
}

/// <summary>
/// DTO for inventory transfer creation response
/// </summary>
public class InventoryTransferCreatedResponseDto
{
    public string Message { get; set; } = "Inventory transfer created successfully";
    public InventoryTransferDto? Transfer { get; set; }
}
