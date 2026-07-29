using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

public class VanSalesTransferRequestResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("doc_entry")]
    public int DocEntry { get; set; }

    [JsonPropertyName("doc_num")]
    public int DocNum { get; set; }
}