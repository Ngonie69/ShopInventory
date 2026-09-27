using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

public class VanSalesLegacyOrderItemDto
{
    [JsonPropertyName("order_id")]
    public int OrderId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }

    [JsonPropertyName("price")]
    public double Price { get; set; }

    [JsonPropertyName("price_total")]
    public double PriceTotal { get; set; }

    /// <summary>How many of this line the office has credited back; 0 when none.</summary>
    [JsonPropertyName("credited_quantity")]
    public double CreditedQuantity { get; set; }

    /// <summary>What was credited back on this line, tax included; 0 when nothing.</summary>
    [JsonPropertyName("credited_amount")]
    public double CreditedAmount { get; set; }
}