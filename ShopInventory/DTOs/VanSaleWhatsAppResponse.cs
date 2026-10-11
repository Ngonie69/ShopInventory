using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

/// <summary>
/// The answer to <c>POST api/vansales/sale/{vanOrder}/whatsapp</c>: the send is queued, or was already —
/// or, asked with no number, the shop has none saved and the rep is to ask for one.
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

    /// <summary>The number, masked to its last four digits; the first of them when there are several.</summary>
    [JsonPropertyName("recipient")]
    public string Recipient { get; set; } = string.Empty;

    /// <summary>Every number the invoice is going to, masked. A shop may have more than one saved.</summary>
    [JsonPropertyName("recipients")]
    public List<string> Recipients { get; set; } = [];

    /// <summary>
    /// True when the request named no number and the shop has none saved: nothing was queued, and the
    /// handset should ask the customer for one.
    /// </summary>
    [JsonPropertyName("needs_number")]
    public bool NeedsNumber { get; set; }

    /// <summary>
    /// True when the number is on the shop's record, so its next invoices go to it without anyone asking.
    /// </summary>
    [JsonPropertyName("saved")]
    public bool Saved { get; set; }

    /// <summary>True when this sale had already been sent to this number; nothing new was queued.</summary>
    [JsonPropertyName("already_requested")]
    public bool AlreadyRequested { get; set; }

    /// <summary>A sentence the handset can show the rep as it is.</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}
