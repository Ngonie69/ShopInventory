using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

public class VanSalesOrderItemBatchRequest
{
    [JsonPropertyName("batch")]
    public string Batch { get; set; } = string.Empty;

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }
}