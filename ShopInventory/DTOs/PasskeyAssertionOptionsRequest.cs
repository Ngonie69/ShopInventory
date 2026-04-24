using System.ComponentModel.DataAnnotations;

namespace ShopInventory.DTOs;

public sealed class PasskeyAssertionOptionsRequest
{
    [Required]
    public string Origin { get; set; } = string.Empty;

    [Required]
    public string RpId { get; set; } = string.Empty;
}