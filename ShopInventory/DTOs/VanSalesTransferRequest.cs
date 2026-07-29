using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

public class VanSalesTransferRequest
{
    [JsonPropertyName("branch")]
    public int Branch { get; set; }

    [JsonPropertyName("warehouse")]
    public string Warehouse { get; set; } = string.Empty;

    [JsonPropertyName("remarks")]
    public string Remarks { get; set; } = string.Empty;

    [JsonPropertyName("doc_date")]
    public string DocDate { get; set; } = string.Empty;

    [JsonPropertyName("items")]
    public List<VanSalesTransferRequestItem> Items { get; set; } = new();
}