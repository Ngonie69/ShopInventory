using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

public class VanSalesLegacyInventoryOrderDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("user")]
    public int User { get; set; }

    [JsonPropertyName("branch")]
    public int Branch { get; set; }

    [JsonPropertyName("warehouse")]
    public string Warehouse { get; set; } = string.Empty;

    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("remarks")]
    public string Remarks { get; set; } = string.Empty;

    [JsonPropertyName("doc_date")]
    public string DocDate { get; set; } = string.Empty;

    [JsonPropertyName("doc_entry")]
    public int? DocEntry { get; set; }

    [JsonPropertyName("doc_num")]
    public int? DocNum { get; set; }

    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("items")]
    public List<VanSalesLegacyInventoryOrderItemDto> Items { get; set; } = new();
}