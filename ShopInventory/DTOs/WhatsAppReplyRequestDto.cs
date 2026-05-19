using System.ComponentModel.DataAnnotations;

namespace ShopInventory.DTOs;

public class WhatsAppReplyRequestDto
{
    [Required]
    public string ChatId { get; set; } = string.Empty;

    [Required]
    public string QuotedMessageId { get; set; } = string.Empty;

    [Required]
    [MaxLength(4096)]
    public string Text { get; set; } = string.Empty;
}