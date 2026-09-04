namespace ShopInventory.DTOs;

/// <summary>
/// Opening a shop.
/// </summary>
/// <remarks>
/// The codes are taken as given rather than checked against SAP, which is how the user management
/// create path already treats the same three values. They reach here from pickers populated out of
/// the master data cache, so a code that is not real cannot ordinarily be sent; adding an SAP
/// round-trip would make opening a shop fail whenever SAP is briefly unreachable, in exchange for
/// catching only a hand-crafted request.
/// </remarks>
public class CreateShopRequest
{
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string BusinessPartnerCode { get; set; } = string.Empty;

    public string WarehouseCode { get; set; } = string.Empty;

    public string? CostCentreCode { get; set; }
}
