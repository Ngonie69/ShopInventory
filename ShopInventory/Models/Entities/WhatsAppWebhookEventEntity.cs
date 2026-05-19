using System.ComponentModel.DataAnnotations;

namespace ShopInventory.Models.Entities;

public class WhatsAppWebhookEventEntity
{
    public int Id { get; set; }

    [MaxLength(255)]
    public string? IdempotencyKey { get; set; }

    [MaxLength(120)]
    public string? DeliveryId { get; set; }

    [Required]
    [MaxLength(100)]
    public string EventType { get; set; } = "unknown";

    [MaxLength(120)]
    public string? SessionName { get; set; }

    [MaxLength(200)]
    public string? MessageId { get; set; }

    [MaxLength(160)]
    public string? ChatId { get; set; }

    [MaxLength(120)]
    public string? SenderNumber { get; set; }

    [MaxLength(160)]
    public string? SenderDisplayName { get; set; }

    [MaxLength(50)]
    public string? MessageType { get; set; }

    [Required]
    [MaxLength(20)]
    public string Direction { get; set; } = "unknown";

    [MaxLength(40)]
    public string? Status { get; set; }

    public bool IsFromMe { get; set; }

    public string? TextBody { get; set; }

    [MaxLength(120)]
    public string? SourcePath { get; set; }

    public DateTime? OccurredAtUtc { get; set; }

    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;

    [Required]
    public string RawPayload { get; set; } = string.Empty;
}