namespace ShopInventory.DTOs;

/// <summary>
/// Vendors read off a filled-in upload template, to check or to add in one go.
/// </summary>
public sealed class ImportVendorsRequest
{
    /// <summary>
    /// True to check every row and report what would happen without saving anything — the page's
    /// preview. False saves, but only when every row passes; one bad row imports nothing.
    /// </summary>
    public bool ValidateOnly { get; set; } = true;

    public List<ImportVendorRow> Rows { get; set; } = [];
}

/// <summary>One vendor as it was typed into the sheet.</summary>
public sealed class ImportVendorRow
{
    /// <summary>The spreadsheet row it came from, so a problem can be pointed at.</summary>
    public int RowNumber { get; set; }

    /// <summary>
    /// The depot's business partner code (COR008) or its warehouse (KEFBYC). Optional when
    /// <see cref="Code"/> is given, because the code's prefix names the warehouse.
    /// </summary>
    public string? Depot { get; set; }

    /// <summary>VMB001, VMP014, VMM003. Blank takes the depot's next free number.</summary>
    public string? Code { get; set; }

    public string? Name { get; set; }

    public string? Surname { get; set; }

    public string? Phone { get; set; }

    public string? Email { get; set; }

    public string? Address { get; set; }

    public string? VatNumber { get; set; }
}
