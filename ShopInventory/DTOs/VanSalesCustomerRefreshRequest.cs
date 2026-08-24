using System.ComponentModel.DataAnnotations;

namespace ShopInventory.DTOs;

/// <summary>Trades a refresh token for a new session.</summary>
public class VanSalesCustomerRefreshRequest
{
    [Required]
    [MaxLength(200)]
    public string RefreshToken { get; set; } = string.Empty;

    [MaxLength(128)]
    public string? DeviceId { get; set; }

    [MaxLength(200)]
    public string? DeviceName { get; set; }
}
