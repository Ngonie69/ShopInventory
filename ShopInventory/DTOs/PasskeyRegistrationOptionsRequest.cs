using System.ComponentModel.DataAnnotations;

namespace ShopInventory.DTOs;

public sealed class PasskeyRegistrationOptionsRequest
{
    [Required]
    public string FriendlyName { get; set; } = string.Empty;

    [Required]
    public string Origin { get; set; } = string.Empty;

    [Required]
    public string RpId { get; set; } = string.Empty;
}