using System.ComponentModel.DataAnnotations;

namespace ShopInventory.DTOs;

/// <summary>Gives a van sales customer an app sign-in, or re-points an existing one.</summary>
public class OnboardVanSalesCustomerAccountRequest
{
    /// <summary>The route customer this sign-in trades as.</summary>
    [Required]
    public int RouteCustomerId { get; set; }

    /// <summary>The handset's number, in whatever form the rep types it.</summary>
    [Required]
    [MaxLength(32)]
    public string PhoneNumber { get; set; } = string.Empty;

    /// <summary>Who holds the phone, for the operator's list.</summary>
    [MaxLength(200)]
    public string? DisplayName { get; set; }

    /// <summary>
    /// What the shop signs in with, alongside its number.
    /// </summary>
    /// <remarks>
    /// Required for a shop that has no sign-in yet. For one that already has, blank keeps the
    /// password already set and anything else replaces it — which is how a forgotten one is reset.
    /// </remarks>
    [MaxLength(72)]
    public string? Password { get; set; }
}
