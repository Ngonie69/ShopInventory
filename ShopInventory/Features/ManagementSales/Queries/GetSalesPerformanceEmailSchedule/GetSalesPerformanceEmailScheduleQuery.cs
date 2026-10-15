using ErrorOr;
using MediatR;

namespace ShopInventory.Features.ManagementSales.Queries.GetSalesPerformanceEmailSchedule;

/// <summary>The management email's schedule, as last saved from Settings.</summary>
public sealed record GetSalesPerformanceEmailScheduleQuery : IRequest<ErrorOr<SalesPerformanceEmailSchedule>>;
