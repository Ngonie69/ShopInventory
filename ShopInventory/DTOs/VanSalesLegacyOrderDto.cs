using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

public class VanSalesLegacyOrderDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("customer_id")]
    public int CustomerId { get; set; }

    [JsonPropertyName("ref")]
    public string Reference { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = string.Empty;

    [JsonPropertyName("item")]
    public int Item { get; set; }

    [JsonPropertyName("units")]
    public int Units { get; set; }

    [JsonPropertyName("price")]
    public double Price { get; set; }

    [JsonPropertyName("doc_date")]
    public string DocDate { get; set; } = string.Empty;

    [JsonPropertyName("due_date")]
    public string DueDate { get; set; } = string.Empty;

    [JsonPropertyName("invoice")]
    public string Invoice { get; set; } = string.Empty;

    [JsonPropertyName("doc_num")]
    public string DocNum { get; set; } = string.Empty;

    [JsonPropertyName("doc_entry")]
    public string DocEntry { get; set; } = string.Empty;

    [JsonPropertyName("purchase_orders")]
    public string PurchaseOrders { get; set; } = string.Empty;

    [JsonPropertyName("fiscalized")]
    public int Fiscalized { get; set; }

    [JsonPropertyName("verification")]
    public string Verification { get; set; } = string.Empty;

    [JsonPropertyName("qrcode")]
    public string QrCode { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("timestamps")]
    public VanSalesLegacyTimestampsDto Timestamps { get; set; } = new();

    [JsonPropertyName("pod")]
    public VanSalesLegacyPodDto Pod { get; set; } = new();

    [JsonPropertyName("order_items")]
    public List<VanSalesLegacyOrderItemDto> OrderItems { get; set; } = new();

    [JsonPropertyName("FiscalizedText")]
    public string FiscalizedText { get; set; } = string.Empty;

    [JsonPropertyName("FiscalizedTextColor")]
    public string FiscalizedTextColor { get; set; } = string.Empty;
}