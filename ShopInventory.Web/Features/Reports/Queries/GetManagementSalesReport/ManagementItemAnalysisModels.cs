using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.Reports.Queries.GetManagementSalesReport;

// The web copies of the API's item drill-down records, property for property.

/// <summary>One item from the management sales report, under the report's own period and filters.</summary>
public sealed record GetManagementItemAnalysisQuery(
    string ItemCode,
    DateTime? FromDate,
    DateTime? ToDate,
    string? WarehouseCode,
    string? SourceSystem,
    string? CardCode = null
) : IRequest<ErrorOr<ManagementItemAnalysisResult>>;

public sealed class ManagementItemAnalysisResult
{
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescription { get; set; }
    public int? ItemsGroupCode { get; set; }
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public DateTime PreviousFromDate { get; set; }
    public DateTime PreviousToDate { get; set; }
    public string? WarehouseCode { get; set; }
    public string? SourceSystem { get; set; }
    public string? CardCode { get; set; }
    public List<ManagementItemCurrencySection> Currencies { get; set; } = [];
    public ManagementMarginStatus Margin { get; set; } = new();
}

public sealed class ManagementItemCurrencySection
{
    public string Currency { get; set; } = string.Empty;
    public ManagementItemSummary Summary { get; set; } = new();
    public List<ManagementItemDayRow> ByDay { get; set; } = [];
    public List<ManagementItemBreakdownRow> ByDepot { get; set; } = [];
    public List<ManagementItemBreakdownRow> ByPartner { get; set; } = [];
    public List<ManagementItemBreakdownRow> ByVendor { get; set; } = [];
    public List<ManagementItemBreakdownRow> ByChannel { get; set; } = [];
    public List<ManagementItemBreakdownRow> ByOperator { get; set; } = [];
}

public sealed class ManagementItemSummary
{
    public decimal Quantity { get; set; }
    public decimal NetAmount { get; set; }
    public int SalesCount { get; set; }
    public decimal AverageUnitPrice { get; set; }
    public decimal UnitsPerSale { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal PreviousQuantity { get; set; }
    public decimal PreviousNetAmount { get; set; }
    public int PreviousSalesCount { get; set; }
    public decimal PreviousAverageUnitPrice { get; set; }
    public decimal? QuantityChangePercent { get; set; }
    public decimal? NetChangePercent { get; set; }
    public decimal? PriceChangePercent { get; set; }
    public decimal? GrossProfit { get; set; }
    public decimal? MarginPercent { get; set; }
}

public sealed class ManagementItemDayRow
{
    public DateTime Date { get; set; }
    public decimal Quantity { get; set; }
    public decimal NetAmount { get; set; }
    public DateTime ComparedDate { get; set; }
    public decimal ComparedQuantity { get; set; }
    public decimal ComparedNetAmount { get; set; }
}

public sealed class ManagementItemBreakdownRow
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Hint { get; set; }
    public decimal Quantity { get; set; }
    public decimal NetAmount { get; set; }
    public int SalesCount { get; set; }
    public decimal ShareOfNetPercent { get; set; }
    public decimal AverageUnitPrice { get; set; }
    public decimal PreviousQuantity { get; set; }
    public decimal PreviousNetAmount { get; set; }
    public decimal? ChangePercent { get; set; }
    public decimal? GrossProfit { get; set; }
    public decimal? MarginPercent { get; set; }
}
