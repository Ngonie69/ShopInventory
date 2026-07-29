namespace ShopInventory.Web.Models;

public class WhatsAppInboxResponseModel
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public List<WhatsAppInboxItemModel> Messages { get; set; } = new();
}