using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

/// <summary>
/// A van rep's report of broken or damaged stock collected from a shop.
/// </summary>
public class VanSalesMarketBreakageRequest
{
    /// <summary>Generated on the handset once per report; a resend with the same id is answered with the first report.</summary>
    [JsonPropertyName("client_request_id")]
    public string ClientRequestId { get; set; } = string.Empty;

    [JsonPropertyName("card_code")]
    public string? CardCode { get; set; }

    [JsonPropertyName("card_name")]
    public string? CardName { get; set; }

    [JsonPropertyName("remarks")]
    public string? Remarks { get; set; }

    /// <summary>When the rep recorded it, as an ISO-8601 instant. Missing means now.</summary>
    [JsonPropertyName("captured_at")]
    public DateTime? CapturedAt { get; set; }

    [JsonPropertyName("items")]
    public List<VanSalesMarketBreakageItem> Items { get; set; } = [];
}

public class VanSalesMarketBreakageItem
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("quantity")]
    public decimal Quantity { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}
