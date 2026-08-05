namespace ShopInventory.Features.Reports.Queries.GetItemVolumeSalesReport;

/// <summary>
/// How the window is cut into the report's period columns.
/// </summary>
/// <remarks>
/// Appended to rather than reordered: the values cross the wire as numbers, so an existing
/// caller's <c>2</c> has to keep meaning monthly.
/// </remarks>
public enum ItemVolumeSalesGrouping
{
    Daily = 0,
    Weekly = 1,
    Monthly = 2,
    Quarterly = 3,

    /// <summary>The whole window as a single period.</summary>
    Total = 4
}
