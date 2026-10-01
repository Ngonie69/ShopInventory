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
/// </remarks>
public sealed record GetAdrPerformanceReportQuery(
    DateTime FromDate,
    DateTime ToDate,
    Guid? UserId = null
) : IRequest<ErrorOr<AdrPerformanceReportResult>>;

/// <summary>
/// Orders and sales are two measures, side by side, and never added together. A van turns an order
/// into an invoice, and that invoice is a sale, so their sum would count the same goods twice.
/// </summary>
public sealed record AdrPerformanceReportResult(
    DateTime FromDate,
    DateTime ToDate,
    AdrPerformanceOverallResult Overall,
    List<AdrPerformanceRepResult> Adrs);

/// <summary>
/// The ADRs as a group, against every van rep.
/// </summary>
/// <remarks>
/// <c>AdrCount</c> is every active ADR account, including those with nothing in the period;
/// <c>ActiveAdrCount</c> those who captured an order or made a sale in it. <c>Orders</c> and
/// <c>Sales</c> set the ADRs' figure against every van rep's, per currency.
/// </remarks>
public sealed record AdrPerformanceOverallResult(
    int AdrCount,
    int ActiveAdrCount,
    AdrOrderCountsResult OrderCounts,
    List<AdrContributionResult> Orders,
    List<AdrContributionResult> Sales);

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
/// orders and sales, per currency.
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
    List<AdrShareResult> Shares)
{
    public string DisplayName => string.IsNullOrWhiteSpace(FullName) ? Username : FullName;
}

/// <summary>
/// One currency's share of every van's trade. Each is null when the vans took nothing in that
/// currency on that measure.
/// </summary>
public sealed record AdrShareResult(
    string Currency,
    double? OrderShare,
    double? SalesShare);
