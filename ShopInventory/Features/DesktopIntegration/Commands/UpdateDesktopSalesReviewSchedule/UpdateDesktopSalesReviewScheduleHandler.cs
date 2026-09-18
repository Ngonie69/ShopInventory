using ErrorOr;
using MediatR;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReviewSchedule;
using ShopInventory.Models;
using ShopInventory.Services;
using static ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReviewSchedule.DesktopSalesReviewScheduleKeys;

namespace ShopInventory.Features.DesktopIntegration.Commands.UpdateDesktopSalesReviewSchedule;

public sealed class UpdateDesktopSalesReviewScheduleHandler(
    ApplicationDbContext db,
    IMediator mediator,
    IAuditService auditService,
    ILogger<UpdateDesktopSalesReviewScheduleHandler> logger)
    : IRequestHandler<UpdateDesktopSalesReviewScheduleCommand, ErrorOr<DesktopSalesReviewSchedule>>
{
    public async Task<ErrorOr<DesktopSalesReviewSchedule>> Handle(
        UpdateDesktopSalesReviewScheduleCommand request, CancellationToken cancellationToken)
    {
        var recipients = Addresses(string.Join(',', request.Recipients));

        await StageAsync(db, WeeklyEnabled, "bool", request.WeeklyEnabled ? "true" : "false",
            "Whether the desktop sales review is emailed every Monday at 07:00 CAT for the week just ended.", cancellationToken);
        await StageAsync(db, MonthlyEnabled, "bool", request.MonthlyEnabled ? "true" : "false",
            "Whether the desktop sales review is emailed on the 1st at 07:00 CAT for the month just ended.", cancellationToken);
        await StageAsync(db, Recipients, "string", string.Join(", ", recipients),
            "Who receives the desktop sales review email, comma-separated.", cancellationToken);
        await StageAsync(db, SendAsUserId, "string", request.CallerUserId.ToString(),
            "The admin the scheduled review is read as: whoever last saved the schedule.", cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Desktop sales review schedule saved by {User}: weekly {Weekly}, monthly {Monthly}, {Count} recipient(s)",
            request.CallerName ?? request.CallerUserId.ToString(), request.WeeklyEnabled, request.MonthlyEnabled, recipients.Count);

        try
        {
            await auditService.LogAsync(
                AuditActions.UpdateDesktopSalesReviewSchedule,
                "DesktopSalesReview",
                null,
                $"Review email weekly {(request.WeeklyEnabled ? "on" : "off")}, monthly {(request.MonthlyEnabled ? "on" : "off")}, "
                    + $"to {(recipients.Count == 0 ? "nobody" : string.Join(", ", recipients))}, saved by {request.CallerName ?? request.CallerUserId.ToString()}",
                true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not audit the desktop sales review schedule change");
        }

        return await mediator.Send(new GetDesktopSalesReviewScheduleQuery(), cancellationToken);
    }
}
