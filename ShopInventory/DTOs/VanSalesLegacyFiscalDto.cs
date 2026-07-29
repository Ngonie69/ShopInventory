using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

public class VanSalesLegacyFiscalDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("verification_code")]
    public string VerificationCode { get; set; } = string.Empty;

    [JsonPropertyName("verification_link")]
    public string VerificationLink { get; set; } = string.Empty;

    [JsonPropertyName("device_id")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("device_serial_number")]
    public string DeviceSerialNumber { get; set; } = string.Empty;

    [JsonPropertyName("fiscal_day")]
    public int FiscalDay { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}