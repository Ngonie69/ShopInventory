namespace ShopInventory.Web.Models;

/// <remarks>
/// Mirrors the API's WhatsAppInboxItemDto, which leaves the raw payload out: an attachment is kept
/// in it whole. <see cref="Services.IWhatsAppAdminService.GetRawPayloadViewAsync"/> reads one.
/// </remarks>
public class WhatsAppInboxItemModel
{
    public int Id { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string? SessionName { get; set; }
    public string? MessageId { get; set; }
    public string? ChatId { get; set; }
    public string? SenderNumber { get; set; }
    public string? SenderDisplayName { get; set; }
    public string? MessageType { get; set; }
    public string Direction { get; set; } = string.Empty;
    public string? Status { get; set; }
    public bool IsFromMe { get; set; }
    public string? TextBody { get; set; }
    public string? SourcePath { get; set; }
    public DateTime? OccurredAtUtc { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
}