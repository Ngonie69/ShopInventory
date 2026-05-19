using System.ComponentModel.DataAnnotations;

namespace ShopInventory.DTOs;

public class WhatsAppCreateSessionRequestDto
{
    [Required]
    [MinLength(3)]
    [MaxLength(50)]
    [RegularExpression("^[a-zA-Z0-9-]+$", ErrorMessage = "Session name can only contain letters, numbers, and hyphens")]
    public string Name { get; set; } = string.Empty;
}