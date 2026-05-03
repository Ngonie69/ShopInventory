namespace ShopInventory.Models;

public sealed class BatchSearchResult
{
    public int BatchEntryId { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemName { get; set; }
    public string BatchNumber { get; set; } = string.Empty;
    public string WarehouseCode { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public string? Status { get; set; }
    public string? ExpiryDate { get; set; }
    public string? ManufacturingDate { get; set; }
    public string? AdmissionDate { get; set; }
    public string? Notes { get; set; }
}