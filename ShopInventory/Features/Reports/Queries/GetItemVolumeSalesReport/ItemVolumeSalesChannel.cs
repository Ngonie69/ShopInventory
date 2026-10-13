namespace ShopInventory.Features.Reports.Queries.GetItemVolumeSalesReport;

/// <summary>
/// The trade channel a business partner sells through, as the report names it.
/// </summary>
/// <remarks>
/// <para>
/// Read from <c>OCRD.U_Channel</c>, the field the office classifies each customer by: Shops, General
/// Trade, Vending, Retail, Wholesale and the rest. The channels sell different ranges, so an item's volume
/// is stated one channel at a time and never added across them — which needs every account in a report to
/// say which channel it is.
/// </para>
/// <para>
/// The value is SAP's own, kept as typed, with one exception. SAP holds the shops under two spellings,
/// <c>Shop</c> and <c>Shops</c>, and they are one channel: splitting them would put half the shops in a
/// group of their own for a typing difference. A partner with nothing in the field is in no channel, which
/// is its own group rather than a guess at one.
/// </para>
/// </remarks>
public static class ItemVolumeSalesChannel
{
    /// <summary>The one spelling the two SAP holds for the shops are read as.</summary>
    public const string Shops = "Shops";

    /// <summary>What an account with no channel in SAP carries: nothing, which a page names for itself.</summary>
    public const string None = "";

    public static string Normalise(string? channel)
    {
        var value = channel?.Trim();

        if (string.IsNullOrEmpty(value))
        {
            return None;
        }

        return string.Equals(value, "Shop", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, Shops, StringComparison.OrdinalIgnoreCase)
            ? Shops
            : value;
    }
}
