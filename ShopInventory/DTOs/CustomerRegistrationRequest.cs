using System.ComponentModel.DataAnnotations;

namespace ShopInventory.DTOs;

public class CustomerRegistrationRequest
{
    [Required]
    [StringLength(50)]
    public string CardCode { get; set; } = string.Empty;

    [Required]
    [StringLength(200)]
    public string CardName { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    [StringLength(100, MinimumLength = 8)]
    public string Password { get; set; } = string.Empty;
}
