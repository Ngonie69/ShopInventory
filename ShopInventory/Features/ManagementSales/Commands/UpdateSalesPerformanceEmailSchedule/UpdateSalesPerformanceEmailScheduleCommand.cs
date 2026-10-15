using ErrorOr;
using MediatR;
using ShopInventory.Features.ManagementSales.Queries.GetSalesPerformanceEmailSchedule;

namespace ShopInventory.Features.ManagementSales.Commands.UpdateSalesPerformanceEmailSchedule;

/// <summary>
/// Saves when the management email goes out and to whom. The admin saving it becomes the account the
/// shop and vending figures are read as.
/// </summary>
public sealed record UpdateSalesPerformanceEmailScheduleCommand(
    Guid CallerUserId,
    string? CallerName,
    bool DailyEnabled,
    bool WeeklyEnabled,
    bool MonthlyEnabled,
    List<string> Recipients
) : IRequest<ErrorOr<SalesPerformanceEmailSchedule>>;
