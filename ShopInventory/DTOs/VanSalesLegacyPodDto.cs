using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

public class VanSalesLegacyPodDto
{
    [JsonPropertyName("order")]
    public int Order { get; set; }

    [JsonPropertyName("images")]
    public string Images { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public int Status { get; set; }
}