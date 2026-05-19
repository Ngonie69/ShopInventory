namespace ShopInventory.DTOs;

public class WhatsAppWebhookReceiptDto
{
    public int Id { get; set; }
    public string EventType { get; set; } = string.Empty;
    public DateTime ReceivedAtUtc { get; set; }
}