namespace ShopInventory.DTOs;

public class BulkPodValidationRequestDto
{
    public List<int> DocNums { get; set; } = [];
    public List<int> SalesOrderDocNums { get; set; } = [];
}