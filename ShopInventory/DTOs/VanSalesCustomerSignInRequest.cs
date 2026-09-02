using System.ComponentModel.DataAnnotations;

namespace ShopInventory.DTOs;

/// <summary>Exchanges a phone number and its password for a session.</summary>
public class VanSalesCustomerSignInRequest
{
    [Required]
    [MaxLength(32)]
    public string PhoneNumber { get; set; } = string.Empty;

    /// <summary>
    /// The password set for this shop.
    /// </summary>
    /// <remarks>
    /// No <c>MinLength</c>, deliberately. Model validation runs before anything reads the database,
    /// so a minimum here would tell a caller which guesses are not worth making without their ever
    /// touching an account — and would lock out any customer whose password was set before the rule.
    /// The length rule belongs where a password is chosen, not where one is presented.
    /// </remarks>
    [Required]
    [MaxLength(72)]
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// A stable identifier for this installation, so a session can be revoked for one handset
    /// without disturbing the customer's other phone. A label, not a secret.
    /// </summary>
    [MaxLength(128)]
    public string? DeviceId { get; set; }

    /// <summary>Handset model, for showing the customer which device a session belongs to.</summary>
    [MaxLength(200)]
    public string? DeviceName { get; set; }
}
