namespace ShopInventory.Web.Features.ItemVolumeConversions;

public sealed class ItemVolumeConversionResult
{
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemName { get; set; }
    public decimal VolumeFactor { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}
