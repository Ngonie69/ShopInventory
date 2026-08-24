using System.ComponentModel.DataAnnotations;

namespace ShopInventory.DTOs;

/// <summary>Asks for a sign-in code to be sent to a van sales customer's phone.</summary>
public class VanSalesCustomerOtpRequest
{
    /// <summary>
    /// The number as the customer types it. Normalised server-side, so local and international
    /// spellings of the same number reach the same account.
    /// </summary>
    [Required]
    [MaxLength(32)]
    public string PhoneNumber { get; set; } = string.Empty;
}
