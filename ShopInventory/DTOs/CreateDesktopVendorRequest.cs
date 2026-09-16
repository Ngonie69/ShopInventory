namespace ShopInventory.DTOs;

/// <summary>
/// A vendor a cart-vendor till adds to its own depot's list.
/// </summary>
/// <remarks>
/// Carries no business partner, for the reason <c>GET DesktopIntegration/vendors</c> takes none: the
/// depot is read off the signed-in account, so a till cannot add a vendor anywhere but where it sells.
///
/// <see cref="Code"/> is optional and normally left out, in which case the platform issues the depot's
/// next number under <c>VendorCodeConvention</c>. It is accepted so that a vendor who was removed can be
/// brought back under the code their earlier sales carry — the same restore an administrator gets.
/// </remarks>
public class CreateDesktopVendorRequest
{
    /// <summary>The depot prefix and three digits, such as <c>VMM014</c>. Blank to take the next one.</summary>
    public string? Code { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Surname { get; set; }

    public string? Phone { get; set; }

    public string? VatNumber { get; set; }
}
