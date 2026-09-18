using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesAnalysis;
using ShopInventory.Features.DesktopIntegration.Queries.GetManagementSalesReport;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReview;

/// <summary>
/// The desktop sales business review: a period's till and vending takings read the way a manager reads
/// them, with the findings worth acting on written out.
/// </summary>
/// <remarks><list type="table">
/// <item><term>Findings</term><description>What the figures say, most urgent first. Written by <see cref="DesktopSalesReviewFindings"/> from the figures below, never typed in.</description></item>
/// <item><term>Currencies</term><description>One section per currency. Currencies are never added together.</description></item>
/// <item><term>Health</term><description>Fiscalisation and SAP posting for the period, from the management report.</description></item>
/// <item><term>Margin</term><description>Whether SAP's booked margin could be read, and on what.</description></item>
/// </list></remarks>
public sealed record DesktopSalesReview(
    DateTime FromDate,
    DateTime ToDate,
    DateTime PreviousFromDate,
    DateTime PreviousToDate,
    string? WarehouseCode,
    DateTime GeneratedAtUtc,
    List<DesktopSalesReviewFinding> Findings,
    List<DesktopSalesReviewCurrency> Currencies,
    ManagementSalesHealth Health,
    ManagementMarginStatus Margin);

/// <summary>One thing the review found.</summary>
/// <remarks><list type="table">
/// <item><term>Code</term><description>Which rule wrote it — stable, for tests and for anyone filtering.</description></item>
/// <item><term>Severity</term><description>One of <see cref="DesktopSalesReviewSeverity"/>: act, check, or know.</description></item>
/// <item><term>Topic</term><description>The section of the review it belongs to, so a page can place it.</description></item>
/// <item><term>Currency</term><description>The currency its figures are in; null when it has none.</description></item>
/// </list></remarks>
public sealed record DesktopSalesReviewFinding(
    string Code,
    string Severity,
    string Topic,
    string? Currency,
    string Title,
    string Detail);

/// <summary>How urgent a finding is.</summary>
public static class DesktopSalesReviewSeverity
{
    /// <summary>Money or compliance at stake; somebody should do something.</summary>
    public const string Action = "action";

    /// <summary>Worth a look; may be deliberate.</summary>
    public const string Review = "review";

    /// <summary>Context for reading the figures.</summary>
    public const string Note = "note";

    public static int Rank(string severity) => severity switch
    {
        Action => 0,
        Review => 1,
        _ => 2
    };
}

/// <summary>Where a finding sits in the review.</summary>
public static class DesktopSalesReviewTopic
{
    public const string Period = "period";
    public const string Trend = "trend";
    public const string Hours = "hours";
    public const string Payments = "payments";
    public const string Shops = "shops";
    public const string Products = "products";
    public const string Pricing = "pricing";
    public const string Tax = "tax";
    public const string Margin = "margin";
    public const string Compliance = "compliance";
}

/// <summary>Everything the review says about one currency.</summary>
/// <remarks><list type="table">
/// <item><term>ByDay</term><description>Each trading day, split by payment method.</description></item>
/// <item><term>ByHour</term><description>Counter hours in CAT, vending settlements shown apart from counter sales.</description></item>
/// <item><term>ByShop</term><description>Each shop or depot, with the per-trading-day figures a fair comparison needs.</description></item>
/// <item><term>ShopHours</term><description>Each shop's sales per hour, one cell per pair that traded.</description></item>
/// <item><term>ShopDays</term><description>Each shop's sales per day, one cell per pair that traded.</description></item>
/// <item><term>TopItems</term><description>The best sellers by value, with their Pareto class.</description></item>
/// <item><term>PriceSpreads</term><description>Items whose realised price differs between shops by more than <see cref="DesktopSalesReviewFindings.PriceSpreadPercent"/>.</description></item>
/// <item><term>LargeSales</term><description>Single counter sales far above their shop's average — trade-size purchases at a till.</description></item>
/// </list></remarks>
public sealed record DesktopSalesReviewCurrency(
    string Currency,
    DesktopSalesReviewHeadline Headline,
    List<DesktopSalesDayRow> ByDay,
    List<DesktopSalesReviewHourRow> ByHour,
    List<DesktopSalesPaymentMethodRow> ByPaymentMethod,
    List<ManagementBreakdownRow> ByChannel,
    List<DesktopSalesReviewShopRow> ByShop,
    List<DesktopSalesReviewShopHourCell> ShopHours,
    List<DesktopSalesReviewShopDayCell> ShopDays,
    List<DesktopSalesReviewItemRow> TopItems,
    int ItemCount,
    decimal TailNetAmount,
    List<DesktopSalesReviewPriceSpread> PriceSpreads,
    List<DesktopSalesReviewLargeSale> LargeSales);

/// <summary>The headline figures, and how the change on the previous period splits.</summary>
/// <remarks><list type="table">
/// <item><term>ShortTendered</term><description>What sales were accepted short: total less a recorded tender that did not cover it.</description></item>
/// <item><term>TrafficEffect</term><description>The takings change explained by more or fewer sales, at the previous average sale.</description></item>
/// <item><term>TicketEffect</term><description>The takings change explained by the average sale moving, at this period's count. The two add up to the change.</description></item>
/// <item><term>EffectiveVatPercent</term><description>VAT over takings before VAT.</description></item>
/// <item><term>MarginPercent</term><description>SAP's gross profit over the revenue it costed, not over all takings.</description></item>
/// </list></remarks>
public sealed record DesktopSalesReviewHeadline(
    int SalesCount,
    decimal TotalAmount,
    decimal NetAmount,
    decimal VatAmount,
    decimal AverageSale,
    decimal QuantitySold,
    int DistinctItems,
    int DaysTraded,
    decimal ShortTendered,
    int PreviousSalesCount,
    decimal PreviousTotalAmount,
    decimal PreviousAverageSale,
    decimal? TotalChangePercent,
    decimal? TrafficEffect,
    decimal? TicketEffect,
    decimal EffectiveVatPercent,
    decimal? GrossProfit,
    decimal? MarginPercent,
    decimal CostedNetAmount);

/// <summary>One hour of the counter day, in CAT.</summary>
/// <remarks><list type="table">
/// <item><term>SettlementSalesCount</term><description>Vending settlements captured in the hour — when a vendor paid in, not when the goods sold.</description></item>
/// </list></remarks>
public sealed record DesktopSalesReviewHourRow(
    int Hour,
    int SalesCount,
    decimal TotalAmount,
    int SettlementSalesCount,
    decimal SettlementTotalAmount);

/// <summary>One shop or depot.</summary>
/// <remarks><list type="table">
/// <item><term>Channel</term><description>The channel most of its sales came through.</description></item>
/// <item><term>FirstSaleDate</term><description>The first sale it ever recorded, up to the end of the period.</description></item>
/// <item><term>StartedInPeriod</term><description>True when that first sale falls inside the period, so its total covers fewer days than the period.</description></item>
/// <item><term>PerTradingDay</term><description>Takings over the days it sold anything: the fair comparison between shops that traded different numbers of days.</description></item>
/// </list></remarks>
public sealed record DesktopSalesReviewShopRow(
    string WarehouseCode,
    string Label,
    string? Hint,
    string Channel,
    int OperatorCount,
    DateTime? FirstSaleDate,
    bool StartedInPeriod,
    int DaysTraded,
    int SalesCount,
    decimal TotalAmount,
    decimal NetAmount,
    decimal VatAmount,
    decimal QuantitySold,
    int DistinctItems,
    decimal ShareOfValuePercent,
    decimal AverageSale,
    decimal PerTradingDay,
    decimal UnitsPerSale,
    decimal NetPerUnit,
    decimal ShortTendered,
    decimal CashAmount,
    decimal NonCashPercent,
    decimal EffectiveVatPercent,
    decimal PreviousTotalAmount,
    decimal? ChangeOnPreviousPercent,
    decimal? GrossProfit,
    decimal? MarginPercent);

public sealed record DesktopSalesReviewShopHourCell(string WarehouseCode, int Hour, int SalesCount, decimal TotalAmount);

public sealed record DesktopSalesReviewShopDayCell(string WarehouseCode, DateTime Date, int SalesCount, decimal TotalAmount);

/// <summary>One best seller.</summary>
/// <remarks><list type="table">
/// <item><term>CumulativeSharePercent</term><description>This item and every one above it, as a share of all line value.</description></item>
/// <item><term>AbcClass</term><description>A while the cumulative share is within 80%, B within 95%, C after — the usual Pareto split.</description></item>
/// <item><term>AverageUnitPrice</term><description>What a unit sold for after discount, before VAT.</description></item>
/// </list></remarks>
public sealed record DesktopSalesReviewItemRow(
    string ItemCode,
    string? ItemDescription,
    decimal Quantity,
    decimal NetAmount,
    int SalesCount,
    decimal ShareOfNetPercent,
    decimal CumulativeSharePercent,
    string AbcClass,
    decimal UnitsPerSale,
    decimal AverageUnitPrice,
    decimal DiscountAmount,
    decimal? ChangePercent,
    decimal? GrossProfit,
    decimal? MarginPercent);

/// <summary>An item that sold for noticeably different prices at different shops.</summary>
/// <remarks><list type="table">
/// <item><term>SpreadPercent</term><description>How far the highest realised price sits above the lowest.</description></item>
/// <item><term>UpliftAtHighPrice</term><description>What the lowest-priced shop's units would have fetched at the highest price, less what they did.</description></item>
/// </list></remarks>
public sealed record DesktopSalesReviewPriceSpread(
    string ItemCode,
    string? ItemDescription,
    int ShopsCompared,
    string LowWarehouseCode,
    decimal LowUnitPrice,
    decimal LowQuantity,
    string HighWarehouseCode,
    decimal HighUnitPrice,
    decimal SpreadPercent,
    decimal UpliftAtHighPrice);

/// <summary>One counter sale far larger than its shop's usual.</summary>
/// <remarks><list type="table">
/// <item><term>CustomerCode</term><description>The customer it was sold to; null for a walk-in sale booked to the shop's own partner.</description></item>
/// <item><term>TimesShopAverage</term><description>How many of its shop's average sale it is worth.</description></item>
/// </list></remarks>
public sealed record DesktopSalesReviewLargeSale(
    int SaleId,
    DateTime DocDate,
    string WarehouseCode,
    string PaymentMethod,
    string? CustomerCode,
    decimal TotalAmount,
    decimal Quantity,
    decimal TimesShopAverage);
