using System.Text.Json.Serialization;

namespace ShopInventory.Models;

/// <summary>
/// Represents an item/product in SAP Business One
/// </summary>
public class Item
{
    [JsonPropertyName("ItemCode")]
    public string? ItemCode { get; set; }

    [JsonPropertyName("ItemName")]
    public string? ItemName { get; set; }

    [JsonPropertyName("ItemType")]
    public string? ItemType { get; set; }

    [JsonPropertyName("ItemsGroupCode")]
    public int? ItemsGroupCode { get; set; }

    [JsonPropertyName("BarCode")]
    public string? BarCode { get; set; }

    [JsonPropertyName("ManageBatchNumbers")]
    public string? ManageBatchNumbers { get; set; }

    [JsonPropertyName("ManageSerialNumbers")]
    public string? ManageSerialNumbers { get; set; }

    [JsonPropertyName("QuantityOnStock")]
    public decimal QuantityOnStock { get; set; }

    [JsonPropertyName("QuantityOrderedFromVendors")]
    public decimal QuantityOrderedFromVendors { get; set; }

    [JsonPropertyName("QuantityOrderedByCustomers")]
    public decimal QuantityOrderedByCustomers { get; set; }

    [JsonPropertyName("InventoryUOM")]
    public string? InventoryUOM { get; set; }

    [JsonPropertyName("SalesUnit")]
    public string? SalesUnit { get; set; }

    [JsonPropertyName("PurchaseUnit")]
    public string? PurchaseUnit { get; set; }

    [JsonPropertyName("DefaultWarehouse")]
    public string? DefaultWarehouse { get; set; }
}

/// <summary>
/// Represents warehouse stock information for an item
/// </summary>
public class ItemWarehouseInfo
{
    [JsonPropertyName("ItemCode")]
    public string? ItemCode { get; set; }

    [JsonPropertyName("WarehouseCode")]
    public string? WarehouseCode { get; set; }

    [JsonPropertyName("InStock")]
    public decimal InStock { get; set; }

    [JsonPropertyName("Committed")]
    public decimal Committed { get; set; }

    [JsonPropertyName("Ordered")]
    public decimal Ordered { get; set; }

    [JsonPropertyName("Available")]
    public decimal Available => InStock - Committed + Ordered;
}

/// <summary>
/// Represents batch number information for an item
/// </summary>
public class BatchNumber
{
    [JsonPropertyName("ItemCode")]
    public string? ItemCode { get; set; }

    [JsonPropertyName("BatchNum")]
    public string? BatchNum { get; set; }

    [JsonPropertyName("Quantity")]
    public decimal Quantity { get; set; }

    [JsonPropertyName("Warehouse")]
    public string? Warehouse { get; set; }

    [JsonPropertyName("Status")]
    public string? Status { get; set; }

    [JsonPropertyName("ManufacturerSerialNumber")]
    public string? ManufacturerSerialNumber { get; set; }

    [JsonPropertyName("InternalSerialNumber")]
    public string? InternalSerialNumber { get; set; }

    [JsonPropertyName("ExpiryDate")]
    public string? ExpiryDate { get; set; }

    [JsonPropertyName("ManufacturingDate")]
    public string? ManufacturingDate { get; set; }

    [JsonPropertyName("AddmisionDate")]
    public string? AdmissionDate { get; set; }

    [JsonPropertyName("Location")]
    public string? Location { get; set; }

    [JsonPropertyName("Notes")]
    public string? Notes { get; set; }
}

/// <summary>
/// Represents the batch numbers collection from SAP
/// </summary>
public class BatchNumberDetails
{
    [JsonPropertyName("ItemCode")]
    public string? ItemCode { get; set; }

    [JsonPropertyName("ItemName")]
    public string? ItemName { get; set; }

    [JsonPropertyName("BatchNumbers")]
    public List<BatchNumber>? BatchNumbers { get; set; }
}
