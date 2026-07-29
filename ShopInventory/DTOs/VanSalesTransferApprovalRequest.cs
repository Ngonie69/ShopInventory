using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

public class VanSalesTransferApprovalRequest
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("status")]
    public int Status { get; set; }
}