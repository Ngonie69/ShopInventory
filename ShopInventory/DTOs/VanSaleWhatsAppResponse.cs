using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

/// <summary>
/// The answer to <c>POST api/vansales/sale/{vanOrder}/whatsapp</c>: the send is queued, or was already.
/// </summary>
/// <remarks>
/// Queued, not sent. The invoice goes once the office has posted the sale to SAP and its fiscal receipt
/// is confirmed — usually within a few minutes, and from a WhatsApp number the customer will not have
/// seen before.
/// </remarks>
public sealed class VanSaleWhatsAppResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; } = true;

    [JsonPropertyName("delivery_id")]
    public long DeliveryId { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    /// <summary>The number, masked to its last four digits.</summary>
    [JsonPropertyName("recipient")]
    public string Recipient { get; set; } = string.Empty;

    /// <summary>True when this sale had already been sent to this number; nothing new was queued.</summary>
    [JsonPropertyName("already_requested")]
    public bool AlreadyRequested { get; set; }

    /// <summary>A sentence the handset can show the rep as it is.</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}
