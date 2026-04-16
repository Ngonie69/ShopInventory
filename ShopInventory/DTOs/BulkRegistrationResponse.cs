namespace ShopInventory.DTOs;

public class BulkRegistrationResponse
{
    public bool Success { get; set; }
    public int Count { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<CustomerRegistrationResponse> Customers { get; set; } = new();
}
