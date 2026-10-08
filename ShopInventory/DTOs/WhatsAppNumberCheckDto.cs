namespace ShopInventory.DTOs;

/// <summary>
/// OpenWA's answer to <c>GET /api/sessions/{id}/contacts/check/{number}</c>: whether a number has a
/// WhatsApp account.
/// </summary>
public sealed class WhatsAppNumberCheckDto
{
    /// <summary>The number as asked, digits only.</summary>
    public string Number { get; set; } = string.Empty;

    public bool Exists { get; set; }

    /// <summary>
    /// The chat id OpenWA would send to. Built from the number asked, not resolved from WhatsApp, so it
    /// says nothing more than <see cref="Exists"/> does.
    /// </summary>
    public string? WhatsappId { get; set; }
}
