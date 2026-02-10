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

#region Transfer Request DTOs

/// <summary>
/// DTO for inventory transfer request response
/// </summary>
public class InventoryTransferRequestDto
{
    public int DocEntry { get; set; }
    public int DocNum { get; set; }
    public string? DocDate { get; set; }
    public string? DueDate { get; set; }
    public string? FromWarehouse { get; set; }
    public string? ToWarehouse { get; set; }
    public string? Comments { get; set; }
    public string? DocumentStatus { get; set; }
    public string? RequesterEmail { get; set; }
    public string? RequesterName { get; set; }
    public int? RequesterBranch { get; set; }
    public int? RequesterDepartment { get; set; }
    public List<InventoryTransferRequestLineDto>? Lines { get; set; }
}

/// <summary>
/// DTO for inventory transfer request line
/// </summary>
public class InventoryTransferRequestLineDto
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
/// Request DTO for creating an inventory transfer request
/// </summary>
public class CreateTransferRequestDto
{
    /// <summary>
    /// Source warehouse code (can be overridden per line)
    /// </summary>
    public string? FromWarehouse { get; set; }

    /// <summary>
    /// Destination warehouse code (required)
    /// </summary>
    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Destination warehouse is required")]
    public string? ToWarehouse { get; set; }

    /// <summary>
    /// Optional document date (defaults to today)
    /// </summary>
    public string? DocDate { get; set; }

    /// <summary>
    /// Optional due date
    /// </summary>
    public string? DueDate { get; set; }

    /// <summary>
    /// Optional comments/notes
    /// </summary>
    public string? Comments { get; set; }

    /// <summary>
    /// Email of the requester
    /// </summary>
    public string? RequesterEmail { get; set; }

    /// <summary>
    /// Name of the requester
    /// </summary>
    public string? RequesterName { get; set; }

    /// <summary>
    /// Branch ID of the requester
    /// </summary>
    public int? RequesterBranch { get; set; }

    /// <summary>
    /// Department ID of the requester
    /// </summary>
    public int? RequesterDepartment { get; set; }

    /// <summary>
    /// Transfer request line items (at least one required)
    /// </summary>
    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "At least one line item is required")]
    [System.ComponentModel.DataAnnotations.MinLength(1, ErrorMessage = "At least one line item is required")]
    public List<CreateTransferRequestLineDto>? Lines { get; set; }
}

/// <summary>
/// Line item for transfer request creation
/// </summary>
public class CreateTransferRequestLineDto
{
    /// <summary>
    /// Item code (required)
    /// </summary>
    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Item code is required")]
    public string? ItemCode { get; set; }

    /// <summary>
    /// Quantity to transfer (must be positive)
    /// </summary>
    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Quantity is required")]
    [System.ComponentModel.DataAnnotations.Range(0.00001, double.MaxValue, ErrorMessage = "Quantity must be greater than zero")]
    public decimal Quantity { get; set; }

    /// <summary>
    /// Source warehouse for this line (overrides header FromWarehouse)
    /// </summary>
    public string? FromWarehouseCode { get; set; }

    /// <summary>
    /// Destination warehouse for this line (overrides header ToWarehouse)
    /// </summary>
    public string? ToWarehouseCode { get; set; }
}

/// <summary>
/// DTO for paginated transfer request list response
/// </summary>
public class TransferRequestListResponseDto
{
    public string? Warehouse { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int Count { get; set; }
    public bool HasMore { get; set; }
    public List<InventoryTransferRequestDto>? TransferRequests { get; set; }
}

/// <summary>
/// DTO for transfer request creation response
/// </summary>
public class TransferRequestCreatedResponseDto
{
    public string Message { get; set; } = "Transfer request created successfully";
    public InventoryTransferRequestDto? TransferRequest { get; set; }
}

/// <summary>
/// DTO for transfer request conversion response
/// </summary>
public class TransferRequestConvertedResponseDto
{
    public string Message { get; set; } = "Transfer request converted successfully";
    public int RequestDocEntry { get; set; }
    public InventoryTransferDto? Transfer { get; set; }
}

#endregion
