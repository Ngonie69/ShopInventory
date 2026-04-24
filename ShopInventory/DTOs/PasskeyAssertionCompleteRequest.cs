using System.ComponentModel.DataAnnotations;

namespace ShopInventory.DTOs;

public sealed class PasskeyAssertionCompleteRequest
{
    [Required]
    public string SessionToken { get; set; } = string.Empty;

    [Required]
    public string CredentialJson { get; set; } = string.Empty;

    [Required]
    public string Origin { get; set; } = string.Empty;

    [Required]
    public string RpId { get; set; } = string.Empty;
}