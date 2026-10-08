namespace ShopInventory.DTOs;

/// <summary>A page of OpenWA's message log, newest first.</summary>
public sealed class WhatsAppMessageHistoryDto
{
    public List<WhatsAppOutboundMessageDto> Messages { get; set; } = [];

    public int Total { get; set; }
}
