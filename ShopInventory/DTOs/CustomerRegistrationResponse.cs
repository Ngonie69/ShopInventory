namespace ShopInventory.DTOs;

public class CustomerRegistrationResponse
{
    public bool Success { get; set; }
    public string CardCode { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
