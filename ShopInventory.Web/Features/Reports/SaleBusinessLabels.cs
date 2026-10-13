namespace ShopInventory.Web.Features.Reports;

/// <summary>
/// What a page calls each line of business the API keys its product rows by.
/// </summary>
/// <remarks>
/// Shops, vending and vans sell different ranges, so the API never ranks an item of one against another's:
/// every product row names the business it belongs to, and a page draws one business at a time. The keys
/// are the API's <c>SaleBusinesses</c>.
/// </remarks>
public static class SaleBusinessLabels
{
    /// <summary>An API that predates the split names no business, and its rows are one unnamed channel.</summary>
    public static string Label(string? business) => business?.Trim().ToLowerInvariant() switch
    {
        "shops" => "Shops",
        "vending" => "Vending",
        "vans" => "Van sales",
        null or "" => "All",
        var other => other
    };
}
