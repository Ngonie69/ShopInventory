namespace ShopInventory.Web.Features.Reports.Queries.GetItemVolumeSalesReport;

/// <summary>
/// How the window is cut into the report's period columns. Mirrors the API enum of the same name,
/// numbering included — the value crosses the wire by name on the way out and as a number on the
/// way back, so both halves have to agree on both.
/// </summary>
public enum ItemVolumeSalesGrouping
{
    Daily = 0,
    Weekly = 1,
    Monthly = 2,
    Quarterly = 3,

    /// <summary>The whole window as a single period.</summary>
    Total = 4
}
