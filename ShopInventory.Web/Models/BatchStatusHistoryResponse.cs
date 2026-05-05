namespace ShopInventory.Web.Models;

public sealed class BatchStatusHistoryResponse
{
    public string SearchTerm { get; set; } = string.Empty;
    public int Page { get; set; }
    public int PageSize { get; set; }
    public bool HasMore { get; set; }
    public List<BatchStatusHistoryItem> Items { get; set; } = new();
}