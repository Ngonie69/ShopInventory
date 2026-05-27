using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

public class VanSalesPodUploadRequest
{
    [JsonPropertyName("order")]
    public int Order { get; set; }

    [JsonPropertyName("images")]
    public List<VanSalesPodUploadImageDto> Images { get; set; } = new();
}