using ErrorOr;
using MediatR;

namespace ShopInventory.Features.ManagementSales.Queries.GetSalesPerformance;

/// <summary>
/// Shops, vending and vans over one period, each against the same number of days just before — what the
/// management email says, as data.
/// </summary>
/// <param name="CallerUserId">Whose read scope the shop and vending figures are read under.</param>
/// <param name="FromDate">First trading day, inclusive.</param>
/// <param name="ToDate">Last trading day, inclusive.</param>
public sealed record GetSalesPerformanceQuery(
    Guid CallerUserId,
    DateTime FromDate,
    DateTime ToDate
) : IRequest<ErrorOr<SalesPerformance>>;
