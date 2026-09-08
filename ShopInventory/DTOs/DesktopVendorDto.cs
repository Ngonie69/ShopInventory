namespace ShopInventory.DTOs;

/// <summary>
/// A vendor as a till needs it: enough to pick one and to name it on a sale.
/// </summary>
/// <remarks>
/// Deliberately narrower than <see cref="RouteCustomerDto"/>. The administrative shape carries who
/// created the row and when, which a counter has no use for, and the till caches whatever it is
/// given — so the smaller answer is also the smaller thing kept on a device.
/// </remarks>
public class DesktopVendorDto
{
    /// <summary>What the sale names. Unique within one business partner's list.</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>The family name. Null for a route customer that is a shop rather than a person.</summary>
    public string? Surname { get; set; }

    public string? Phone { get; set; }

    /// <summary>The vendor's VAT registration, where they have one.</summary>
    public string? VatNumber { get; set; }
}
