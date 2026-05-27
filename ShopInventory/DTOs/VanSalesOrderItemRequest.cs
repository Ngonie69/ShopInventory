using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

public class VanSalesOrderItemRequest
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }

    [JsonPropertyName("price")]
    public double Price { get; set; }

    [JsonPropertyName("batches")]
    public List<VanSalesOrderItemBatchRequest> Batches { get; set; } = new();
}