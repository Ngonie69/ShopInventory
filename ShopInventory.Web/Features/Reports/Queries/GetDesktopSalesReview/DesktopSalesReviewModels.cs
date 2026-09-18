// Generated from ShopInventory/Features/DesktopIntegration/Queries/GetDesktopSalesReview/DesktopSalesReview.cs by mirror_records.py: the API records, as the Web mirrors them.
// Nullability matches the API exactly; regenerate rather than hand-edit.
using ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesAnalysis;
using ShopInventory.Web.Features.Reports.Queries.GetManagementSalesReport;

namespace ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesReview;

public sealed class DesktopSalesReview
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public DateTime PreviousFromDate { get; set; }
    public DateTime PreviousToDate { get; set; }
    public string? WarehouseCode { get; set; }
    public DateTime GeneratedAtUtc { get; set; }
    public List<DesktopSalesReviewFinding> Findings { get; set; } = [];
    public List<DesktopSalesReviewCurrency> Currencies { get; set; } = [];
    public ManagementSalesHealth Health { get; set; } = new();
    public ManagementMarginStatus Margin { get; set; } = new();
}

public sealed class DesktopSalesReviewFinding
{
    public string Code { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Topic { get; set; } = string.Empty;
    public string? Currency { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
}

public sealed class DesktopSalesReviewCurrency
{
    public string Currency { get; set; } = string.Empty;
    public DesktopSalesReviewHeadline Headline { get; set; } = new();
    public List<DesktopSalesDayRow> ByDay { get; set; } = [];
    public List<DesktopSalesReviewHourRow> ByHour { get; set; } = [];
    public List<DesktopSalesPaymentMethodRow> ByPaymentMethod { get; set; } = [];
    public List<ManagementBreakdownRow> ByChannel { get; set; } = [];
    public List<DesktopSalesReviewShopRow> ByShop { get; set; } = [];
    public List<DesktopSalesReviewShopHourCell> ShopHours { get; set; } = [];
    public List<DesktopSalesReviewShopDayCell> ShopDays { get; set; } = [];
    public List<DesktopSalesReviewItemRow> TopItems { get; set; } = [];
    public int ItemCount { get; set; }
    public decimal TailNetAmount { get; set; }
    public List<DesktopSalesReviewPriceSpread> PriceSpreads { get; set; } = [];
    public List<DesktopSalesReviewLargeSale> LargeSales { get; set; } = [];
}

public sealed class DesktopSalesReviewHeadline
{
    public int SalesCount { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal NetAmount { get; set; }
    public decimal VatAmount { get; set; }
    public decimal AverageSale { get; set; }
    public decimal QuantitySold { get; set; }
    public int DistinctItems { get; set; }
    public int DaysTraded { get; set; }
    public decimal ShortTendered { get; set; }
    public int PreviousSalesCount { get; set; }
    public decimal PreviousTotalAmount { get; set; }
    public decimal PreviousAverageSale { get; set; }
    public decimal? TotalChangePercent { get; set; }
    public decimal? TrafficEffect { get; set; }
    public decimal? TicketEffect { get; set; }
    public decimal EffectiveVatPercent { get; set; }
    public decimal? GrossProfit { get; set; }
    public decimal? MarginPercent { get; set; }
    public decimal CostedNetAmount { get; set; }
}

public sealed class DesktopSalesReviewHourRow
{
    public int Hour { get; set; }
    public int SalesCount { get; set; }
    public decimal TotalAmount { get; set; }
    public int SettlementSalesCount { get; set; }
    public decimal SettlementTotalAmount { get; set; }
}

public sealed class DesktopSalesReviewShopRow
{
    public string WarehouseCode { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Hint { get; set; }
    public string Channel { get; set; } = string.Empty;
    public int OperatorCount { get; set; }
    public DateTime? FirstSaleDate { get; set; }
    public bool StartedInPeriod { get; set; }
    public int DaysTraded { get; set; }
    public int SalesCount { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal NetAmount { get; set; }
    public decimal VatAmount { get; set; }
    public decimal QuantitySold { get; set; }
    public int DistinctItems { get; set; }
    public decimal ShareOfValuePercent { get; set; }
    public decimal AverageSale { get; set; }
    public decimal PerTradingDay { get; set; }
    public decimal UnitsPerSale { get; set; }
    public decimal NetPerUnit { get; set; }
    public decimal ShortTendered { get; set; }
    public decimal CashAmount { get; set; }
    public decimal NonCashPercent { get; set; }
    public decimal EffectiveVatPercent { get; set; }
    public decimal PreviousTotalAmount { get; set; }
    public decimal? ChangeOnPreviousPercent { get; set; }
    public decimal? GrossProfit { get; set; }
    public decimal? MarginPercent { get; set; }
}

public sealed class DesktopSalesReviewShopHourCell
{
    public string WarehouseCode { get; set; } = string.Empty;
    public int Hour { get; set; }
    public int SalesCount { get; set; }
    public decimal TotalAmount { get; set; }
}

public sealed class DesktopSalesReviewShopDayCell
{
    public string WarehouseCode { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public int SalesCount { get; set; }
    public decimal TotalAmount { get; set; }
}

public sealed class DesktopSalesReviewItemRow
{
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescription { get; set; }
    public decimal Quantity { get; set; }
    public decimal NetAmount { get; set; }
    public int SalesCount { get; set; }
    public decimal ShareOfNetPercent { get; set; }
    public decimal CumulativeSharePercent { get; set; }
    public string AbcClass { get; set; } = string.Empty;
    public decimal UnitsPerSale { get; set; }
    public decimal AverageUnitPrice { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal? ChangePercent { get; set; }
    public decimal? GrossProfit { get; set; }
    public decimal? MarginPercent { get; set; }
}

public sealed class DesktopSalesReviewPriceSpread
{
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescription { get; set; }
    public int ShopsCompared { get; set; }
    public string LowWarehouseCode { get; set; } = string.Empty;
    public decimal LowUnitPrice { get; set; }
    public decimal LowQuantity { get; set; }
    public string HighWarehouseCode { get; set; } = string.Empty;
    public decimal HighUnitPrice { get; set; }
    public decimal SpreadPercent { get; set; }
    public decimal UpliftAtHighPrice { get; set; }
}

public sealed class DesktopSalesReviewLargeSale
{
    public int SaleId { get; set; }
    public DateTime DocDate { get; set; }
    public string WarehouseCode { get; set; } = string.Empty;
    public string PaymentMethod { get; set; } = string.Empty;
    public string? CustomerCode { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal Quantity { get; set; }
    public decimal TimesShopAverage { get; set; }
}
