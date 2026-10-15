using ErrorOr;
using MediatR;
using ShopInventory.Data;
using ShopInventory.Features.ManagementSales.Queries.GetSalesPerformanceEmailSchedule;
using ShopInventory.Models;
using ShopInventory.Services;
using static ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReviewSchedule.DesktopSalesReviewScheduleKeys;
using Keys = ShopInventory.Features.ManagementSales.Queries.GetSalesPerformanceEmailSchedule.SalesPerformanceEmailScheduleKeys;

namespace ShopInventory.Features.ManagementSales.Commands.UpdateSalesPerformanceEmailSchedule;

public sealed class UpdateSalesPerformanceEmailScheduleHandler(
    ApplicationDbContext db,
    IMediator mediator,
    IAuditService auditService,
    ILogger<UpdateSalesPerformanceEmailScheduleHandler> logger)
    : IRequestHandler<UpdateSalesPerformanceEmailScheduleCommand, ErrorOr<SalesPerformanceEmailSchedule>>
{
    public async Task<ErrorOr<SalesPerformanceEmailSchedule>> Handle(
        UpdateSalesPerformanceEmailScheduleCommand request, CancellationToken cancellationToken)
    {
        var recipients = Addresses(string.Join(',', request.Recipients));

        await Keys.StageAsync(db, Keys.Enabled(SalesPerformanceEmailCadence.Daily), "bool", request.DailyEnabled ? "true" : "false",
            "Whether the management sales performance email goes out every morning at 07:30 CAT for the day before.", cancellationToken);
        await Keys.StageAsync(db, Keys.Enabled(SalesPerformanceEmailCadence.Weekly), "bool", request.WeeklyEnabled ? "true" : "false",
            "Whether the management sales performance email goes out every Monday at 07:30 CAT for the week just ended.", cancellationToken);
        await Keys.StageAsync(db, Keys.Enabled(SalesPerformanceEmailCadence.Monthly), "bool", request.MonthlyEnabled ? "true" : "false",
            "Whether the management sales performance email goes out on the 1st at 07:30 CAT for the month just ended.", cancellationToken);
        await Keys.StageAsync(db, Keys.Recipients, "string", string.Join(", ", recipients),
            "Who receives the management sales performance email, comma-separated.", cancellationToken);
        await Keys.StageAsync(db, Keys.SendAsUserId, "string", request.CallerUserId.ToString(),
            "The admin the scheduled email's shop and vending figures are read as: whoever last saved the schedule.", cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Sales performance email schedule saved by {User}: daily {Daily}, weekly {Weekly}, monthly {Monthly}, {Count} recipient(s)",
            request.CallerName ?? request.CallerUserId.ToString(), request.DailyEnabled, request.WeeklyEnabled, request.MonthlyEnabled, recipients.Count);

        try
        {
            await auditService.LogAsync(
                AuditActions.UpdateSalesPerformanceEmailSchedule,
                "SalesPerformanceEmail",
                null,
                $"Management sales email daily {OnOff(request.DailyEnabled)}, weekly {OnOff(request.WeeklyEnabled)}, monthly {OnOff(request.MonthlyEnabled)}, "
                    + $"to {(recipients.Count == 0 ? "nobody" : string.Join(", ", recipients))}, saved by {request.CallerName ?? request.CallerUserId.ToString()}",
                true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not audit the sales performance email schedule change");
        }

        return await mediator.Send(new GetSalesPerformanceEmailScheduleQuery(), cancellationToken);
    }

    private static string OnOff(bool on) => on ? "on" : "off";
}
