using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

/// <summary>
/// The body of <c>POST api/vansales/sale/{vanOrder}/whatsapp</c>: send that sale's invoice to the shop's
/// saved WhatsApp number, or to the number the customer gave at the sale.
/// </summary>
public sealed class VanSaleWhatsAppRequest
{
    /// <summary>
    /// As the customer said it: <c>0771234567</c>, <c>+263 77 123 4567</c> and so on. Left out, the
    /// invoice goes to the number already saved on the shop, and the answer says <c>needs_number</c>
    /// when there is none.
    /// </summary>
    [JsonPropertyName("phone")]
    public string? Phone { get; set; }

    /// <summary>
    /// The rep confirms the customer asked for the invoice on this number. Required with a
    /// <see cref="Phone"/>; a saved number is sent to on the consent it was saved under.
    /// </summary>
    [JsonPropertyName("consent")]
    public bool Consent { get; set; }
}
