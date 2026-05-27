using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

public class VanSalesLegacyTimestampsDto
{
    [JsonPropertyName("create_date")]
    public string CreateDate { get; set; } = string.Empty;

    [JsonPropertyName("approval_date")]
    public string ApprovalDate { get; set; } = string.Empty;

    [JsonPropertyName("delivery_date")]
    public string DeliveryDate { get; set; } = string.Empty;
}