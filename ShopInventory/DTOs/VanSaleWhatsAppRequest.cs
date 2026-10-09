using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

/// <summary>
/// The body of <c>POST api/vansales/sale/{vanOrder}/whatsapp</c>: the number a customer gave at the sale,
/// to send that sale's invoice to.
/// </summary>
public sealed class VanSaleWhatsAppRequest
{
    /// <summary>As the customer said it: <c>0771234567</c>, <c>+263 77 123 4567</c> and so on.</summary>
    [JsonPropertyName("phone")]
    public string? Phone { get; set; }

    /// <summary>The rep confirms the customer asked for the invoice on this number. Required.</summary>
    [JsonPropertyName("consent")]
    public bool Consent { get; set; }
}
