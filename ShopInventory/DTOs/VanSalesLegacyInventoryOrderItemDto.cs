using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

public class VanSalesLegacyInventoryOrderItemDto
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }

    [JsonPropertyName("price")]
    public double Price { get; set; }

    [JsonPropertyName("warehouse")]
    public string Warehouse { get; set; } = string.Empty;

    [JsonPropertyName("product")]
    public VanSalesLegacyInventoryProductDto Product { get; set; } = new();
}