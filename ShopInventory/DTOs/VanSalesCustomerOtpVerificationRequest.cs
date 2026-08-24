using System.ComponentModel.DataAnnotations;

namespace ShopInventory.DTOs;

/// <summary>Exchanges a code for a session.</summary>
public class VanSalesCustomerOtpVerificationRequest
{
    [Required]
    [MaxLength(32)]
    public string PhoneNumber { get; set; } = string.Empty;

    [Required]
    [MaxLength(12)]
    public string Code { get; set; } = string.Empty;

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
