using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

public class VanSalesPodUploadImageDto
{
    [JsonPropertyName("image")]
    public string Image { get; set; } = string.Empty;
}