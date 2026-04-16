namespace ShopInventory.DTOs;

public class BulkRegistrationRequest
{
    public string? DefaultPassword { get; set; }
    public List<CustomerBasicInfo> Customers { get; set; } = new();
}
