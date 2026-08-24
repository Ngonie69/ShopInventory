using System.ComponentModel.DataAnnotations;

namespace ShopInventory.DTOs;

/// <summary>
/// Ends a session. With neither field supplied, every session on the account ends — which is what
/// someone who has lost the device actually wants.
/// </summary>
public class VanSalesCustomerLogoutRequest
{
    [MaxLength(200)]
    public string? RefreshToken { get; set; }

    [MaxLength(128)]
    public string? DeviceId { get; set; }
}
