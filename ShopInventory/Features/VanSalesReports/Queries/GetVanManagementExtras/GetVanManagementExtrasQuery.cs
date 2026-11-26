using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesReports.Queries.GetVanManagementExtras;

/// <summary>
/// What the van sales management report needs beyond the daily truck report: how many of a chosen set
/// of products each truck sold each day, and the market breakages each truck brought back.
/// </summary>
/// <remarks>
/// The office's "Vansales Daily Report" workbook typed both by hand: a column per 5 litre Shamiso flavour
/// on every truck-day, and a Friday breakages log. Both are in the system already, on the sale lines and
/// on the handset's market breakage reports, and are read out here on the same truck-day grain as
/// <see cref="GetVanDailyTruckReport.GetVanDailyTruckReportQuery"/> so the two lay side by side.
/// </remarks>
/// <param name="FromDate">First trading day, CAT, inclusive.</param>
/// <param name="ToDate">Last trading day, CAT, inclusive.</param>
/// <param name="ItemCodes">The products to count; none counts nothing and still returns the breakages.</param>
public sealed record GetVanManagementExtrasQuery(DateTime FromDate, DateTime ToDate, IReadOnlyCollection<string> ItemCodes)
    : IRequest<ErrorOr<VanManagementExtrasResult>>;

/// <param name="FromDate">First trading day, CAT, inclusive.</param>
/// <param name="ToDate">Last trading day, CAT, inclusive.</param>
/// <param name="Products">The chosen products sold, one row per route per day per item.</param>
/// <param name="Breakages">Market breakage lines reported in the period, rejected reports left out.</param>
/// <param name="ProductUnitsOutsideRoutes">Units of the chosen products sold by logins on no route.</param>
public sealed record VanManagementExtrasResult(
    DateTime FromDate,
    DateTime ToDate,
    IReadOnlyList<VanProductDayDto> Products,
    IReadOnlyList<VanBreakageLineDto> Breakages,
    decimal ProductUnitsOutsideRoutes);

/// <summary>One item sold by one truck on one day, in US dollars.</summary>
/// <param name="RouteId">The route the truck ran that day.</param>
/// <param name="TradingDate">The CAT trading day.</param>
/// <param name="ItemCode">The item.</param>
/// <param name="ItemName">The item's description as sold; null when none was recorded.</param>
/// <param name="Units">Quantity in the unit it was sold in.</param>
/// <param name="Value">The lines' value, VAT included.</param>
public sealed record VanProductDayDto(
    int RouteId,
    DateTime TradingDate,
    string ItemCode,
    string? ItemName,
    decimal Units,
    decimal Value);

/// <summary>One line of a market breakage report.</summary>
/// <param name="BreakageId">The report the line belongs to.</param>
/// <param name="RouteId">The route of the reporting rep's truck on the day; null when the rep is on none.</param>
/// <param name="TradingDate">The CAT day the rep recorded it.</param>
/// <param name="ReportedBy">The rep's name.</param>
/// <param name="CustomerName">The shop it was collected from; null when none was named.</param>
/// <param name="ItemCode">The item.</param>
/// <param name="ItemDescription">The item's description; null when none was recorded.</param>
/// <param name="Reason">The cause the rep chose; null when none was given.</param>
/// <param name="Units">The office's confirmed count where there is one, else what the rep reported.</param>
/// <param name="IsConfirmed">The office has counted it.</param>
/// <param name="Status">Where the report is: Pending, Transferred, TransferFailed or Transferring.</param>
/// <param name="ValueBeforeVat">
/// The units at the average price before VAT the vans sold the item for in the period; null when the
/// vans sold none of it.
/// </param>
public sealed record VanBreakageLineDto(
    int BreakageId,
    int? RouteId,
    DateTime TradingDate,
    string ReportedBy,
    string? CustomerName,
    string ItemCode,
    string? ItemDescription,
    string? Reason,
    decimal Units,
    bool IsConfirmed,
    string Status,
    decimal? ValueBeforeVat);
