using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesReports.Queries.GetAdrPerformanceReport;

/// <summary>
/// How each ADR performed over a period, and what the ADRs together contributed to the vans' trade.
/// </summary>
/// <remarks>
/// <c>FromDate</c> and <c>ToDate</c> are inclusive CAT trading days, like every van sales report.
/// <c>UserId</c> narrows the rows to one ADR; the contribution figures are still measured against
/// every van, because "what share did this ADR bring in" has no meaning against a total of one.
/// With <c>UserId</c> the result also carries that ADR's <see cref="AdrPerformanceDetailResult"/>,
/// whose items are ranked and cut at <c>TopItems</c> (zero or less keeps them all).
/// </remarks>
public sealed record GetAdrPerformanceReportQuery(
    DateTime FromDate,
    DateTime ToDate,
    Guid? UserId = null,
    int TopItems = 25
) : IRequest<ErrorOr<AdrPerformanceReportResult>>;

/// <summary>
/// Orders and sales are two measures, side by side, and never added together. A van turns an order
/// into an invoice, and that invoice is a sale, so their sum would count the same goods twice.
/// </summary>
/// <remarks>
/// <c>OrdersByChannel</c> widens the whole beyond the vans: every sales order raised in the period,
/// split by who raised it. <c>Detail</c> is present only when the query names one ADR.
/// <c>Caveats</c> says what the figures cannot see, and is empty when there is nothing to say.
/// </remarks>
public sealed record AdrPerformanceReportResult(
    DateTime FromDate,
    DateTime ToDate,
    AdrPerformanceOverallResult Overall,
    List<AdrPerformanceRepResult> Adrs,
    List<AdrChannelResult> OrdersByChannel,
    AdrPerformanceDetailResult? Detail,
    List<string> Caveats);

/// <summary>
/// The ADRs as a group, against every van rep.
/// </summary>
/// <remarks>
/// <c>AdrCount</c> is every active ADR account, including those with nothing in the period;
/// <c>ActiveAdrCount</c> those who captured an order or made a sale in it. <c>Orders</c> and
/// <c>Sales</c> set the ADRs' figure against every van rep's, per currency.
///
/// <c>Calls</c> are the shops the ADRs checked in at on the van app, one per shop per day, and are
/// null when no ADR recorded a visit. <c>ProductiveCalls</c> are shops that ordered or bought on a
/// day, counted once however many documents they gave.
/// </remarks>
public sealed record AdrPerformanceOverallResult(
    int AdrCount,
    int ActiveAdrCount,
    AdrOrderCountsResult OrderCounts,
    List<AdrContributionResult> Orders,
    List<AdrContributionResult> Sales,
    int? Calls,
    int ProductiveCalls)
{
    /// <summary>Null when nothing was visited: unmeasured, not 0%.</summary>
    public double? StrikeRate => Calls is > 0 ? (double)ProductiveCalls / Calls.Value : null;
}

/// <summary>Where a set of sales orders stands.</summary>
/// <remarks>
/// <c>InSap</c> carries a SAP document number; <c>Fulfilled</c> the van has invoiced, in full or in
/// part; <c>Pending</c> is not yet in SAP and not withdrawn; <c>Cancelled</c> was cancelled or
/// rejected.
/// </remarks>
public sealed record AdrOrderCountsResult(
    int Total,
    int InSap,
    int Fulfilled,
    int Pending,
    int Cancelled);

/// <summary>
/// One currency's ADR figure set against the same figure for every van rep.
/// </summary>
public sealed record AdrContributionResult(
    string Currency,
    int AdrDocumentCount,
    decimal AdrGross,
    int VanDocumentCount,
    decimal VanGross)
{
    /// <summary>Null, not zero, when the vans took nothing in this currency.</summary>
    public double? Share => VanGross > 0 ? (double)(AdrGross / VanGross) : null;
}

/// <summary>
/// One ADR's period.
/// </summary>
/// <remarks>
/// <c>VanAccountCode</c> is the business partner the ADR's van posts against. <c>ActiveDays</c> are
/// trading days with an order captured or a sale made. The two customer counts are distinct shops,
/// and leave out documents that recorded no shop. <c>Shares</c> are this ADR's share of every van's
/// orders and sales, per currency. <c>Calls</c> and <c>ProductiveCalls</c> mean what they do on
/// <see cref="AdrPerformanceOverallResult"/>; <c>Calls</c> is null when this ADR recorded no visits.
/// </remarks>
public sealed record AdrPerformanceRepResult(
    Guid UserId,
    string Username,
    string? FullName,
    string? VanAccountCode,
    bool IsActive,
    int ActiveDays,
    AdrOrderCountsResult OrderCounts,
    int OrderCustomerCount,
    List<VanSalesMoneyResult> OrderTotalsByCurrency,
    int SaleCustomerCount,
    List<VanSalesMoneyResult> SalesTotalsByCurrency,
    List<AdrShareResult> Shares,
    int? Calls,
    int ProductiveCalls)
{
    public string DisplayName => string.IsNullOrWhiteSpace(FullName) ? Username : FullName;

    /// <summary>
    /// Null when the ADR recorded no visits. Can exceed 1 where orders exist with no recorded visit,
    /// and is not clamped — that gap is worth seeing.
    /// </summary>
    public double? StrikeRate => Calls is > 0 ? (double)ProductiveCalls / Calls.Value : null;
}

/// <summary>
/// One currency's share of every van's trade. Each is null when the vans took nothing in that
/// currency on that measure.
/// </summary>
public sealed record AdrShareResult(
    string Currency,
    double? OrderShare,
    double? SalesShare);

// ── Who raised the order book ───────────────────────────────────────────────────

/// <summary>
/// One group's part of every sales order raised in the period, whatever its source.
/// </summary>
/// <remarks>
/// Grouped on the raiser's role today, or on the source for the customer ordering app, which has no
/// staff raiser. Cancelled and rejected orders are out of the part and the whole. <c>Shares</c> are
/// per currency.
/// </remarks>
public sealed record AdrChannelResult(
    string Channel,
    bool IsAdr,
    int OrderCount,
    int PeopleCount,
    List<AdrChannelShareResult> Shares);

/// <summary>One currency's value for a group, and its share of every order in that currency.</summary>
public sealed record AdrChannelShareResult(
    string Currency,
    decimal Gross,
    double? Share);

// ── One ADR ─────────────────────────────────────────────────────────────────────

/// <summary>One ADR's shops, the items they ordered, and their orders.</summary>
/// <remarks>
/// <c>Orders</c> is the newest <see cref="GetAdrPerformanceReportHandler.MaximumOrdersListed"/>;
/// <c>OrdersNotListed</c> counts the rest, which every figure above still includes.
/// </remarks>
public sealed record AdrPerformanceDetailResult(
    Guid UserId,
    List<AdrShopResult> Shops,
    List<AdrItemResult> Items,
    List<AdrOrderResult> Orders,
    int OrdersNotListed);

/// <summary>
/// A shop the ADR ordered for or sold to. Orders and sales stay in separate columns: an order the
/// van then invoiced would otherwise be counted twice.
/// </summary>
public sealed record AdrShopResult(
    string CustomerCode,
    string? CustomerName,
    int OrderCount,
    List<VanSalesMoneyResult> OrderTotalsByCurrency,
    int SaleCount,
    List<VanSalesMoneyResult> SalesTotalsByCurrency,
    DateTime LastActiveOn);

/// <summary>
/// An item the ADR ordered, ranked on reach — lines and shops — because value cannot be compared
/// across currencies nor quantity across units. Value is line totals, not order totals.
/// </summary>
public sealed record AdrItemResult(
    int Rank,
    string ItemCode,
    string? ItemDescription,
    int LineCount,
    int ShopCount,
    List<VanSalesQuantityResult> QuantitiesByUoM,
    List<VanSalesLineMoneyResult> TotalsByCurrency);

/// <summary>
/// One order. <c>Stage</c> is one of "Not yet in SAP", "In SAP", "Fulfilled" and "Cancelled" — the
/// same four <see cref="AdrOrderCountsResult"/> counts, except that a fulfilled order is shown as
/// fulfilled even though it is also in SAP.
/// </summary>
public sealed record AdrOrderResult(
    int Id,
    string OrderNumber,
    int? SapDocNum,
    DateTime TradingDate,
    string? CustomerCode,
    string? CustomerName,
    string Status,
    string Stage,
    string Currency,
    decimal DocTotal,
    int LineCount);
