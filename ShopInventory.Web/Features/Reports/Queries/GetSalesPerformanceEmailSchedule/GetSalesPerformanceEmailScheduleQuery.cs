using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.Reports.Queries.GetSalesPerformanceEmailSchedule;

/// <summary>When the management sales performance email goes out and to whom.</summary>
public sealed record GetSalesPerformanceEmailScheduleQuery : IRequest<ErrorOr<SalesPerformanceEmailSchedule>>;
