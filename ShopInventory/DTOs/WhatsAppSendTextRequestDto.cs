using System.ComponentModel.DataAnnotations;

namespace ShopInventory.DTOs;

public class WhatsAppSendTextRequestDto
{
    [Required]
    public string ChatId { get; set; } = string.Empty;

    [Required]
    [MaxLength(4096)]
    public string Text { get; set; } = string.Empty;
}