namespace ShopInventory.DTOs;

/// <summary>
/// DTO for packaging material stock information
/// </summary>
public class PackagingMaterialStockDto
{
    public string? ItemCode { get; set; }
    public string? ItemName { get; set; }
    public decimal InStock { get; set; }
    public decimal Available { get; set; }
    public string? UoM { get; set; }
}

/// <summary>
/// DTO for stock quantity information in a warehouse
/// </summary>
public class StockQuantityDto
{
    public string? ItemCode { get; set; }
    public string? ItemName { get; set; }
    public string? BarCode { get; set; }
    public string? WarehouseCode { get; set; }
    public decimal InStock { get; set; }
    public decimal Committed { get; set; }
    public decimal Ordered { get; set; }
    public decimal Available { get; set; }
    public string? UoM { get; set; }

    // Packaging code fields
    public string? PackagingCode { get; set; }
    public string? PackagingCodeLabels { get; set; }
    public string? PackagingCodeLids { get; set; }

    // Packaging material stock quantities (populated when packaging codes are present)
    public PackagingMaterialStockDto? PackagingMaterialStock { get; set; }
    public PackagingMaterialStockDto? PackagingLabelStock { get; set; }
    public PackagingMaterialStockDto? PackagingLidStock { get; set; }
}

/// <summary>
/// Response DTO for stock quantities in a warehouse
/// </summary>
public class WarehouseStockResponseDto
{
    public string? WarehouseCode { get; set; }
    public int TotalItems { get; set; }
    public int ItemsInStock { get; set; }
    public DateTime QueryDate { get; set; }
    public List<StockQuantityDto>? Items { get; set; }
}

/// <summary>
/// Response DTO for paginated stock quantities
/// </summary>
public class WarehouseStockPagedResponseDto
{
    public string? WarehouseCode { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int Count { get; set; }
    public bool HasMore { get; set; }
    public DateTime QueryDate { get; set; }
    public List<StockQuantityDto>? Items { get; set; }
}

/// <summary>
/// DTO for sales quantity information
/// </summary>
public class SalesQuantityDto
{
    public string? ItemCode { get; set; }
    public string? ItemName { get; set; }
    public string? BarCode { get; set; }
    public decimal TotalQuantitySold { get; set; }
    public decimal TotalSalesValue { get; set; }
    public int InvoiceCount { get; set; }
    public string? UoM { get; set; }

    // Packaging code fields
    public string? PackagingCode { get; set; }
    public string? PackagingCodeLabels { get; set; }
    public string? PackagingCodeLids { get; set; }
}

/// <summary>
/// Response DTO for sales quantities in a warehouse over a period
/// </summary>
public class WarehouseSalesResponseDto
{
    public string? WarehouseCode { get; set; }
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public int TotalItemsSold { get; set; }
    public decimal TotalSalesValue { get; set; }
    public int TotalInvoices { get; set; }
    public List<SalesQuantityDto>? Items { get; set; }
}

/// <summary>
/// Request DTO for sales query
/// </summary>
public class SalesQueryRequestDto
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
}

/// <summary>
/// DTO for warehouse information
/// </summary>
public class WarehouseDto
{
    public string? WarehouseCode { get; set; }
    public string? WarehouseName { get; set; }
    public string? Location { get; set; }
    public string? Street { get; set; }
    public string? City { get; set; }
    public string? Country { get; set; }
    public bool IsActive { get; set; }
}

/// <summary>
/// Response DTO for list of warehouses
/// </summary>
public class WarehouseListResponseDto
{
    public int TotalWarehouses { get; set; }
    public List<WarehouseDto>? Warehouses { get; set; }
}

/// <summary>
/// Represents a stock validation error when attempting to create an invoice
/// </summary>
public class StockValidationError
{
    /// <summary>
    /// The line number in the invoice request (1-based)
    /// </summary>
    public int LineNumber { get; set; }

    /// <summary>
    /// The item code that has insufficient stock
    /// </summary>
    public string? ItemCode { get; set; }

    /// <summary>
    /// The item name/description
    /// </summary>
    public string? ItemName { get; set; }

    /// <summary>
    /// The warehouse code where stock was checked
    /// </summary>
    public string? WarehouseCode { get; set; }

    /// <summary>
    /// The quantity requested in the invoice
    /// </summary>
    public decimal RequestedQuantity { get; set; }

    /// <summary>
    /// The available quantity in stock
    /// </summary>
    public decimal AvailableQuantity { get; set; }

    /// <summary>
    /// The shortage amount (RequestedQuantity - AvailableQuantity)
    /// </summary>
    public decimal Shortage => RequestedQuantity - AvailableQuantity;

    /// <summary>
    /// The batch number if this is a batch-specific validation error
    /// </summary>
    public string? BatchNumber { get; set; }

    /// <summary>
    /// Human-readable error message
    /// </summary>
    public string Message => BatchNumber != null
        ? $"Insufficient stock for item '{ItemCode}' batch '{BatchNumber}' in warehouse '{WarehouseCode}'. Requested: {RequestedQuantity}, Available: {AvailableQuantity}, Shortage: {Shortage}"
        : $"Insufficient stock for item '{ItemCode}' in warehouse '{WarehouseCode}'. Requested: {RequestedQuantity}, Available: {AvailableQuantity}, Shortage: {Shortage}";
}

/// <summary>
/// Response when stock validation fails
/// </summary>
public class StockValidationResponseDto
{
    public string Message { get; set; } = "Stock validation failed";
    public bool IsValid { get; set; }
    public List<StockValidationError>? Errors { get; set; }

    /// <summary>
    /// Non-critical warnings that don't prevent the operation
    /// </summary>
    public List<string>? Warnings { get; set; }

    /// <summary>
    /// Suggestions for resolving stock issues
    /// </summary>
    public List<string>? Suggestions { get; set; }
}
