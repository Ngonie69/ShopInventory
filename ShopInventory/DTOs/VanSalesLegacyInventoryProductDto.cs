using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

public class VanSalesLegacyInventoryProductDto
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("price")]
    public double Price { get; set; }

    [JsonPropertyName("price_zig")]
    public double PriceZig { get; set; }

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }

    [JsonPropertyName("prices_list")]
    public List<object> PricesList { get; set; } = new();
}