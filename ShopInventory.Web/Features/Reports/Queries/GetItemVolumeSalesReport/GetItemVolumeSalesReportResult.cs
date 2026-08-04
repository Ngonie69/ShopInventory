namespace ShopInventory.Web.Features.Reports.Queries.GetItemVolumeSalesReport;

public sealed class GetItemVolumeSalesReportResult
{
    public DateTime GeneratedAtUtc { get; set; }
    public DateTime FromDateUtc { get; set; }
    public DateTime ToDateUtc { get; set; }
    public ItemVolumeSalesGrouping Grouping { get; set; }

    public List<string> RequestedAccountCodes { get; set; } = new();

    /// <summary>Empty when the caller asked for every item.</summary>
    public List<string> RequestedItemCodes { get; set; } = new();

    /// <summary>
    /// Item codes that moved in the window but have no active conversion factor. These are the
    /// codes an administrator needs to add on the conversion maintenance screen.
    /// </summary>
    public List<string> ItemCodesWithoutFactor { get; set; } = new();

    public ItemVolumeSalesSummaryResult Summary { get; set; } = new();

    /// <summary>Per item across every selected account.</summary>
    public List<ItemVolumeSalesItemResult> ItemTotals { get; set; } = new();

    /// <summary>Per account across the whole window.</summary>
    public List<ItemVolumeSalesAccountResult> AccountTotals { get; set; } = new();

    public List<ItemVolumeSalesPeriodResult> Periods { get; set; } = new();

    public List<ItemVolumeSalesDocumentLineResult> DocumentLines { get; set; } = new();
}
