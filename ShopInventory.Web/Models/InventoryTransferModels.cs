using System.Text.Json.Serialization;

namespace ShopInventory.Web.Models;

public class InventoryTransferDto
{
    [JsonPropertyName("docEntry")]
    public int DocEntry { get; set; }

    [JsonPropertyName("docNum")]
    public int DocNum { get; set; }

    [JsonPropertyName("docDate")]
    public string? DocDate { get; set; }

    [JsonPropertyName("dueDate")]
    public string? DueDate { get; set; }

    [JsonPropertyName("fromWarehouse")]
    public string? FromWarehouse { get; set; }

    [JsonPropertyName("toWarehouse")]
    public string? ToWarehouse { get; set; }

    [JsonPropertyName("comments")]
    public string? Comments { get; set; }

    [JsonPropertyName("lines")]
    public List<InventoryTransferLineDto>? Lines { get; set; }
}

public class InventoryTransferLineDto
{
    [JsonPropertyName("lineNum")]
    public int LineNum { get; set; }

    [JsonPropertyName("itemCode")]
    public string? ItemCode { get; set; }

    [JsonPropertyName("itemDescription")]
    public string? ItemDescription { get; set; }

    [JsonPropertyName("quantity")]
    public decimal Quantity { get; set; }

    [JsonPropertyName("fromWarehouseCode")]
    public string? FromWarehouseCode { get; set; }

    [JsonPropertyName("toWarehouseCode")]
    public string? ToWarehouseCode { get; set; }

    [JsonPropertyName("uoMCode")]
    public string? UoMCode { get; set; }
}

public class InventoryTransferListResponse
{
    [JsonPropertyName("warehouse")]
    public string? Warehouse { get; set; }

    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("hasMore")]
    public bool HasMore { get; set; }

    [JsonPropertyName("transfers")]
    public List<InventoryTransferDto>? Transfers { get; set; }
}

public class InventoryTransferDateResponse
{
    [JsonPropertyName("warehouse")]
    public string? Warehouse { get; set; }

    [JsonPropertyName("date")]
    public string? Date { get; set; }

    [JsonPropertyName("fromDate")]
    public string? FromDate { get; set; }

    [JsonPropertyName("toDate")]
    public string? ToDate { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("transfers")]
    public List<InventoryTransferDto>? Transfers { get; set; }
}

#region Create Inventory Transfer Models

/// <summary>
/// Request DTO for creating an inventory transfer
/// </summary>
public class CreateInventoryTransferDto
{
    public string? FromWarehouse { get; set; }
    public string? ToWarehouse { get; set; }
    public string? DocDate { get; set; }
    public string? DueDate { get; set; }
    public string? Comments { get; set; }
    public List<CreateInventoryTransferLineDto> Lines { get; set; } = new();
}

/// <summary>
/// Line item for inventory transfer creation
/// </summary>
public class CreateInventoryTransferLineDto
{
    public string? ItemCode { get; set; }
    public decimal Quantity { get; set; }
    public string? FromWarehouseCode { get; set; }
    public string? ToWarehouseCode { get; set; }
}

/// <summary>
/// DTO for inventory transfer creation response
/// </summary>
public class InventoryTransferCreatedResponse
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("transfer")]
    public InventoryTransferDto? Transfer { get; set; }
}

#endregion

#region Transfer Request Models

/// <summary>
/// DTO for inventory transfer request response
/// </summary>
public class InventoryTransferRequestDto
{
    [JsonPropertyName("docEntry")]
    public int DocEntry { get; set; }

    [JsonPropertyName("docNum")]
    public int DocNum { get; set; }

    [JsonPropertyName("docDate")]
    public string? DocDate { get; set; }

    [JsonPropertyName("dueDate")]
    public string? DueDate { get; set; }

    [JsonPropertyName("fromWarehouse")]
    public string? FromWarehouse { get; set; }

    [JsonPropertyName("toWarehouse")]
    public string? ToWarehouse { get; set; }

    [JsonPropertyName("comments")]
    public string? Comments { get; set; }

    [JsonPropertyName("documentStatus")]
    public string? DocumentStatus { get; set; }

    [JsonPropertyName("requesterEmail")]
    public string? RequesterEmail { get; set; }

    [JsonPropertyName("requesterName")]
    public string? RequesterName { get; set; }

    [JsonPropertyName("requesterBranch")]
    public int? RequesterBranch { get; set; }

    [JsonPropertyName("requesterDepartment")]
    public int? RequesterDepartment { get; set; }

    [JsonPropertyName("lines")]
    public List<InventoryTransferRequestLineDto>? Lines { get; set; }
}

/// <summary>
/// DTO for inventory transfer request line
/// </summary>
public class InventoryTransferRequestLineDto
{
    [JsonPropertyName("lineNum")]
    public int LineNum { get; set; }

    [JsonPropertyName("itemCode")]
    public string? ItemCode { get; set; }

    [JsonPropertyName("itemDescription")]
    public string? ItemDescription { get; set; }

    [JsonPropertyName("quantity")]
    public decimal Quantity { get; set; }

    [JsonPropertyName("fromWarehouseCode")]
    public string? FromWarehouseCode { get; set; }

    [JsonPropertyName("toWarehouseCode")]
    public string? ToWarehouseCode { get; set; }

    [JsonPropertyName("uoMCode")]
    public string? UoMCode { get; set; }
}

/// <summary>
/// Request DTO for creating an inventory transfer request
/// </summary>
public class CreateTransferRequestDto
{
    public string? FromWarehouse { get; set; }
    public string? ToWarehouse { get; set; }
    public string? DocDate { get; set; }
    public string? DueDate { get; set; }
    public string? Comments { get; set; }
    public string? RequesterEmail { get; set; }
    public string? RequesterName { get; set; }
    public int? RequesterBranch { get; set; }
    public int? RequesterDepartment { get; set; }
    public List<CreateTransferRequestLineDto> Lines { get; set; } = new();
}

/// <summary>
/// Line item for transfer request creation
/// </summary>
public class CreateTransferRequestLineDto
{
    public string? ItemCode { get; set; }
    public decimal Quantity { get; set; }
    public string? FromWarehouseCode { get; set; }
    public string? ToWarehouseCode { get; set; }
}

/// <summary>
/// DTO for paginated transfer request list response
/// </summary>
public class TransferRequestListResponse
{
    [JsonPropertyName("warehouse")]
    public string? Warehouse { get; set; }

    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("hasMore")]
    public bool HasMore { get; set; }

    [JsonPropertyName("transferRequests")]
    public List<InventoryTransferRequestDto>? TransferRequests { get; set; }
}

/// <summary>
/// DTO for transfer request creation response
/// </summary>
public class TransferRequestCreatedResponse
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("transferRequest")]
    public InventoryTransferRequestDto? TransferRequest { get; set; }
}

/// <summary>
/// DTO for transfer request conversion response
/// </summary>
public class TransferRequestConvertedResponse
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("requestDocEntry")]
    public int RequestDocEntry { get; set; }

    [JsonPropertyName("transfer")]
    public InventoryTransferDto? Transfer { get; set; }
}

#endregion
