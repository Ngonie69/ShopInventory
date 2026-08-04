namespace ShopInventory.Features.ItemVolumeConversions.Queries.GetItemVolumeConversions;

public sealed class GetItemVolumeConversionsResult
{
    public int TotalCount { get; set; }
    public int ActiveCount { get; set; }
    public List<ItemVolumeConversionResult> Conversions { get; set; } = new();
}
