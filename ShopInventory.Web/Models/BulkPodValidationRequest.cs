namespace ShopInventory.Web.Models;

public class BulkPodValidationRequest
{
    public List<int> DocNums { get; set; } = [];
    public List<int> SalesOrderDocNums { get; set; } = [];
}