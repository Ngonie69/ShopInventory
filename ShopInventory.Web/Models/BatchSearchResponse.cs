namespace ShopInventory.Web.Models;

public sealed class BatchSearchResponse
{
    public string SearchTerm { get; set; } = string.Empty;
    public int ResultCount { get; set; }
    public List<BatchSearchItem> Results { get; set; } = new();
}