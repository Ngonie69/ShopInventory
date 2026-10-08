namespace ShopInventory.DTOs;

/// <summary>An OpenWA session documents could be sent from.</summary>
public sealed class WhatsAppSessionOptionDto
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string? Phone { get; set; }
}
