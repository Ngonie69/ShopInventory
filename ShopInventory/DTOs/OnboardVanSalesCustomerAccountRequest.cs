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
}
