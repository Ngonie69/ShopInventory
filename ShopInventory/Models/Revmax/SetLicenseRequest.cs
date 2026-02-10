namespace ShopInventory.Models.Revmax;

/// <summary>
/// Request DTO for setting license.
/// </summary>
public class SetLicenseRequest
{
    /// <summary>
    /// License key to set.
    /// </summary>
    public string? License { get; set; }
}
