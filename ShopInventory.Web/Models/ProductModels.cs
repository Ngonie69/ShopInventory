namespace ShopInventory.Web.Models;

public class ProductDto
{
    public string? ItemCode { get; set; }
    public string? ItemName { get; set; }
    public string? BarCode { get; set; }
    public string? ItemType { get; set; }
    public bool ManagesBatches { get; set; }
    public decimal QuantityInStock { get; set; }
    public decimal QuantityAvailable { get; set; }
    public decimal QuantityCommitted { get; set; }
    public decimal QuantityOnStock { get; set; }
    public decimal Price { get; set; }
    public string? DefaultWarehouse { get; set; }
    public string? UoM { get; set; }
    public List<BatchDto>? Batches { get; set; }
}

public class BatchDto
{
    public string? BatchNumber { get; set; }
    public decimal Quantity { get; set; }
    public string? Status { get; set; }
    public string? ExpiryDate { get; set; }
    public string? ManufacturingDate { get; set; }
    public string? AdmissionDate { get; set; }
    public string? Location { get; set; }
    public string? Notes { get; set; }
}

public class WarehouseProductsResponse
{
    public string? WarehouseCode { get; set; }
    public int TotalProducts { get; set; }
    public int ProductsWithBatches { get; set; }
    public List<ProductDto>? Products { get; set; }
}

public class WarehouseProductsPagedResponse
{
    public string? WarehouseCode { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int Count { get; set; }
    public bool HasMore { get; set; }
    public List<ProductDto>? Products { get; set; }
}

public class ProductBatchesResponse
{
    public string? WarehouseCode { get; set; }
    public string? ItemCode { get; set; }
    public string? ItemName { get; set; }
    public decimal TotalQuantity { get; set; }
    public int BatchCount { get; set; }
    public List<BatchDto>? Batches { get; set; }
}

public class ProductsResponse
{
    public int Count { get; set; }
    public List<ProductDto>? Products { get; set; }
}
