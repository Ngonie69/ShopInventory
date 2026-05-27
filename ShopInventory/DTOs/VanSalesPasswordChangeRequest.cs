using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

public class VanSalesPasswordChangeRequest
{
    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;
}