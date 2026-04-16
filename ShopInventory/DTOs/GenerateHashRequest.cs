using System.ComponentModel.DataAnnotations;

namespace ShopInventory.DTOs;

public class GenerateHashRequest
{
    [Required]
    public string Password { get; set; } = string.Empty;
}
