using ErrorOr;
using MediatR;
using ShopInventory.Features.VanSalesReports.Queries.GetAdrPerformanceReport;

namespace ShopInventory.Features.VanSalesReports.Queries.GetOrderConversionReport;

/// <summary>
/// How much of what the van sales reps and the ADRs ordered became actual van sales, split by who
/// raised each order, with a projection and the areas worth a manager's attention.
/// </summary>
/// <remarks>
/// <c>FromDate</c> and <c>ToDate</c> are inclusive CAT trading days, like every van sales report, and
/// select orders by the day they were captured. <c>Role</c> ("Sales" or "ADR") and <c>UserId</c> narrow
/// the orders the report follows; the split by raiser and the share of van sales always cover every
/// van order, because they are comparisons. <c>ComparePrevious</c> measures the period before as well.
/// </remarks>
public sealed record GetOrderConversionReportQuery(
    DateTime FromDate,
    DateTime ToDate,
    string? Role = null,
    Guid? UserId = null,
    bool ComparePrevious = true
) : IRequest<ErrorOr<OrderConversionReportResult>>;

/// <summary>
/// Van sales orders followed to the sales they became.
/// </summary>
/// <remarks>
/// <para>Each order is followed through the invoice queue entry the van's conversion writes, exactly as
/// the ADR report does (<see cref="AdrConversion"/>), so an order invoiced after the period still counts
/// and the two reports cannot disagree about an ADR's orders.</para>
/// <para>Money is per currency and includes VAT on both sides. <c>LeadCurrency</c> is the one most was
/// ordered in; the projection and the attention list value things in it.</para>
/// <para><c>Orders</c> lists the newest <see cref="GetOrderConversionReportHandler.MaximumOrdersListed"/>
/// orders; <c>OrdersNotListed</c> counts the rest, which every figure still includes.</para>
/// </remarks>
public sealed record OrderConversionReportResult(
    DateTime FromDate,
    DateTime ToDate,
    string? LeadCurrency,
    AdrConversionSummaryResult Overall,
    List<OrderConversionRaiserResult> Raisers,
    List<OrderConversionRepResult> Reps,
    List<OrderConversionSalesMixResult> SalesMix,
    OrderConversionProductsResult Products,
    List<OrderConversionDayResult> Days,
    List<OrderConversionShopResult> Shops,
    List<OrderConversionLedgerResult> Orders,
    int OrdersNotListed,
    List<OrderConversionProjectionResult> Projections,
    List<OrderConversionAttentionResult> Attention,
    OrderConversionComparisonResult? Previous,
    List<string> Caveats);

/// <summary>Who raises van orders. The role is the raiser's today.</summary>
public static class OrderRaisers
{
    public const string SalesReps = "Sales";
    public const string Adrs = "ADR";

    public static string Label(string role) => role == Adrs ? "ADRs" : "Sales reps";
}

/// <summary>
/// One kind of raiser's orders followed to the sale. <c>People</c> are active accounts with that role;
/// <c>ActivePeople</c> those who raised an order in the period.
/// </summary>
public sealed record OrderConversionRaiserResult(
    string Role,
    string Label,
    int People,
    int ActivePeople,
    AdrConversionSummaryResult Summary);

/// <summary>One rep's orders followed to the sale.</summary>
public sealed record OrderConversionRepResult(
    Guid UserId,
    string Username,
    string? FullName,
    string Role,
    string? VanAccountCode,
    string? RouteCode,
    string? RouteName,
    bool IsActive,
    AdrConversionSummaryResult Summary)
{
    public string DisplayName => string.IsNullOrWhiteSpace(FullName) ? Username : FullName;
}

/// <summary>
/// One currency of every van sale made in the period, split by where it came from: a converted order
/// (by who raised it) or a sale the van made on the spot.
/// </summary>
/// <remarks>
/// Sale-dated, unlike the order-dated figures: this answers "of what the vans sold in these days, how
/// much came from orders". <c>FromOtherOrders</c> is orders raised by anyone else — the customer
/// ordering app, the office.
/// </remarks>
public sealed record OrderConversionSalesMixResult(
    string Currency,
    int SaleCount,
    decimal VanSales,
    int FromOrdersCount,
    decimal FromSalesRepOrders,
    decimal FromAdrOrders,
    decimal FromOtherOrders)
{
    public decimal FromOrders => FromSalesRepOrders + FromAdrOrders + FromOtherOrders;

    public decimal Direct => VanSales - FromOrders;

    public double? FromOrdersShare => VanSales > 0 ? (double)(FromOrders / VanSales) : null;
}

/// <summary>
/// The products on the orders against what the vans delivered of them.
/// </summary>
/// <remarks>
/// Units are summed across items in each order's own unit, so <c>UnitsOrdered</c> is a count of
/// pieces, cases and crates together; the per-item rows are the reliable comparison.
/// <c>UnitsOrdered</c> covers every live order, <c>UnitsOnInvoicedOrders</c> only the ones the van
/// invoiced, and <c>UnitsSupplied</c> what those invoices carried of the ordered items.
/// </remarks>
public sealed record OrderConversionProductsResult(
    int ProductsOrdered,
    int ProductsSupplied,
    int ProductsShort,
    int LinesOrdered,
    decimal UnitsOrdered,
    decimal UnitsOnInvoicedOrders,
    decimal UnitsSupplied,
    decimal UnitsNotInvoiced,
    List<OrderConversionItemResult> Items)
{
    public double? UnitConversion => UnitsOrdered > 0 ? Math.Min(1d, (double)(UnitsSupplied / UnitsOrdered)) : null;
}

/// <summary>
/// One product across every order in scope.
/// </summary>
/// <remarks>
/// <c>OrderedQuantity</c> is every live order; <c>SuppliedQuantity</c> and <c>ShortQuantity</c> are on
/// invoiced orders; <c>NotInvoicedQuantity</c> is on live orders with no invoice. <c>SuppliedQuantity</c> is what
/// the van delivered against the order, so a van that sent more than was ordered does not hide a shortfall elsewhere. <c>Converted</c> money is the
/// ordered value of what was delivered, at the order's prices.
/// </remarks>
public sealed record OrderConversionItemResult(
    string ItemCode,
    string? ItemDescription,
    string? UoMCode,
    int Orders,
    int Shops,
    decimal OrderedQuantity,
    decimal SuppliedQuantity,
    decimal ShortQuantity,
    decimal NotInvoicedQuantity,
    decimal SalesRepQuantity,
    decimal AdrQuantity,
    List<OrderConversionItemMoneyResult> Money)
{
    public double? UnitConversion => OrderedQuantity > 0 ? Math.Min(1d, (double)(SuppliedQuantity / OrderedQuantity)) : null;
}

public sealed record OrderConversionItemMoneyResult(
    string Currency,
    decimal Ordered,
    decimal Converted,
    decimal ShortSupplied);

/// <summary>One trading day's orders in one currency, split by raiser, and what they became.</summary>
public sealed record OrderConversionDayResult(
    DateTime TradingDate,
    string Currency,
    int Orders,
    decimal SalesRepOrdered,
    decimal SalesRepInvoiced,
    decimal AdrOrdered,
    decimal AdrInvoiced);

/// <summary>A shop the orders were for, across every rep who ordered for it.</summary>
public sealed record OrderConversionShopResult(
    string CustomerCode,
    string? CustomerName,
    string RaisedBy,
    int Orders,
    int Invoiced,
    int ShortSupplied,
    int Uninvoiced,
    List<AdrDayMoneyResult> Money,
    DateTime? LastOrderOn);

/// <summary>One order beside the invoice it became, with who raised it.</summary>
public sealed record OrderConversionLedgerResult(
    AdrLedgerOrderResult Order,
    string Role);

/// <summary>
/// Where this currency is heading at the period's pace.
/// </summary>
/// <remarks>
/// <para><c>SettledConversion</c> is over orders at least two days old, which have had time to be
/// invoiced; the orders younger than that are expected to end the same way, and
/// <c>ExpectedFromOpenOrders</c> is what that adds to the sales already made.</para>
/// <para>The horizon is the rest of the month <c>ToDate</c> is in, or the whole next month when less than
/// a week of it is left; the month-end figures still cover the remainder. It counts only the weekdays that had orders in the period, and
/// <c>OrderedPerDay</c> is the period's ordered value over the same weekdays.</para>
/// <para><c>WeeklyTrendPoints</c> is the least-squares slope of the weekly conversion, in percentage
/// points a week, over at least three weeks; null otherwise.</para>
/// </remarks>
public sealed record OrderConversionProjectionResult(
    string Currency,
    int OrderDays,
    decimal OrderedPerDay,
    decimal ConvertedPerDay,
    double? SettledConversion,
    decimal OpenOrdersValue,
    decimal ExpectedFromOpenOrders,
    double? ProjectedPeriodConversion,
    string HorizonLabel,
    DateTime HorizonFrom,
    DateTime HorizonTo,
    int HorizonDays,
    decimal ProjectedOrdered,
    decimal ProjectedConverted,
    decimal? ProjectedMonthOrdered,
    decimal? ProjectedMonthConverted,
    double TargetConversion,
    decimal UpsideAtTarget,
    double? WeeklyTrendPoints,
    List<OrderConversionWeekResult> Weeks)
{
    public decimal ProjectedLeakage => ProjectedOrdered - ProjectedConverted;
}

public sealed record OrderConversionWeekResult(
    DateTime WeekStart,
    int Orders,
    decimal Ordered,
    decimal Invoiced)
{
    public double? Conversion => Ordered > 0 ? (double)(Invoiced / Ordered) : null;
}

/// <summary>
/// Something a manager should look at, ranked most pressing first.
/// </summary>
/// <remarks>
/// <c>Severity</c> is "high", "medium" or "low". <c>ValueAtStake</c> is in <c>Currency</c> — the lead
/// currency — and null when the point is not about money. <c>Focus</c> names the part of the page that
/// shows the detail: "reps", "items", "shops" or "orders:&lt;outcome&gt;".
/// </remarks>
public sealed record OrderConversionAttentionResult(
    string Severity,
    string Area,
    string Title,
    string Detail,
    string? Currency,
    decimal? ValueAtStake,
    string? Focus);

/// <summary>The period before, measured the same way, for the deltas.</summary>
public sealed record OrderConversionComparisonResult(
    DateTime FromDate,
    DateTime ToDate,
    AdrConversionSummaryResult Overall,
    List<OrderConversionRaiserResult> Raisers);
