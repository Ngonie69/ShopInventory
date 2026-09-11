namespace ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesAnalysis;

/// <summary>
/// A period's till takings, one section per currency. Mirrors the API's result of the same name.
/// </summary>
public sealed class DesktopSalesAnalysisResult
{
    public DateTime FromDate { get; set; }

    public DateTime ToDate { get; set; }

    /// <summary>The warehouse the API confined the analysis to, or null for every shop.</summary>
    public string? WarehouseCode { get; set; }

    public string? SourceSystem { get; set; }

    public DateTime GeneratedAtUtc { get; set; }

    /// <summary>The payment methods every breakdown is split by, in drawing order.</summary>
    public List<string> PaymentMethods { get; set; } = [];

    public List<DesktopSalesCurrencyAnalysis> Currencies { get; set; } = [];
}
