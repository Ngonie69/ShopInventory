using System.ComponentModel.DataAnnotations;

namespace ShopInventory.DTOs;

/// <summary>A customer handset reporting its push token.</summary>
public class RegisterVanSalesCustomerDeviceRequest
{
    /// <summary>The FCM registration token. Sent on sign-in and whenever Firebase rotates it.</summary>
    [Required]
    [MaxLength(512)]
    public string DeviceToken { get; set; } = string.Empty;

    [MaxLength(128)]
    public string? DeviceId { get; set; }

    [MaxLength(200)]
    public string? DeviceName { get; set; }

    [MaxLength(50)]
    public string? AppVersion { get; set; }
}
