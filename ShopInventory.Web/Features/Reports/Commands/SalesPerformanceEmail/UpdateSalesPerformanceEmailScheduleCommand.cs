using ErrorOr;
using MediatR;
using ShopInventory.Web.Features.Reports.Queries.GetSalesPerformanceEmailSchedule;

namespace ShopInventory.Web.Features.Reports.Commands.SalesPerformanceEmail;

/// <summary>Switches the daily, weekly and monthly management emails on or off and sets who receives them.</summary>
public sealed record UpdateSalesPerformanceEmailScheduleCommand(bool DailyEnabled, bool WeeklyEnabled, bool MonthlyEnabled, List<string> Recipients)
    : IRequest<ErrorOr<SalesPerformanceEmailSchedule>>;
