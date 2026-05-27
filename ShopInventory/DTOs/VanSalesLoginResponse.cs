using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

public class VanSalesLoginResponse
{
    [JsonPropertyName("user")]
    public VanSalesLoginUserDto User { get; set; } = null!;

    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("shop")]
    public List<VanSalesShopDto> Shop { get; set; } = new();

    [JsonPropertyName("type")]
    public string Type { get; set; } = "Bearer";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("rate")]
    public int Rate { get; set; }

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = string.Empty;

    [JsonPropertyName("expires_at")]
    public DateTime ExpiresAt { get; set; }
}