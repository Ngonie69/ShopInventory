using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

public class VanSalesOrderRequest
{
    [JsonPropertyName("customer")]
    public int Customer { get; set; }

    [JsonPropertyName("customer_code")]
    public string? CustomerCode { get; set; }

    [JsonPropertyName("ref")]
    public string Reference { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = string.Empty;

    [JsonPropertyName("amount_paid")]
    public double AmountPaid { get; set; }

    [JsonPropertyName("change")]
    public double Change { get; set; }

    [JsonPropertyName("due_date")]
    public string DueDate { get; set; } = string.Empty;

    [JsonPropertyName("latitude")]
    public string Latitude { get; set; } = string.Empty;

    [JsonPropertyName("auto_post")]
    public int AutoPost { get; set; }

    [JsonPropertyName("van_order")]
    public string VanOrder { get; set; } = string.Empty;

    [JsonPropertyName("longitude")]
    public string Longitude { get; set; } = string.Empty;

    [JsonPropertyName("sales_order")]
    public string SalesOrder { get; set; } = string.Empty;

    [JsonPropertyName("sales_order_id")]
    public int? SalesOrderId { get; set; }

    [JsonPropertyName("items")]
    public List<VanSalesOrderItemRequest> Items { get; set; } = new();
}