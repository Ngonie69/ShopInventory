using MediatR;
using Quartz;
using ShopInventory.Features.DesktopIntegration.Commands.SendDesktopSalesReviewEmail;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReviewSchedule;

namespace ShopInventory.Services;

/// <summary>
/// Emails the desktop sales business review: the week just ended on Mondays, the month just ended on
/// the 1st, both at 07:00 CAT.
/// </summary>
/// <remarks>
/// Runs every morning and asks for both, rather than firing only on the right days. The send remembers
/// which period it last mailed, so on an ordinary day this does nothing — and a Monday the server was
/// down is caught up the next morning instead of being lost. Whether each is switched on, who receives
/// it and whose scope it is read under are all saved from Web → Settings and read on every run.
/// </remarks>
[DisallowConcurrentExecution]
public sealed class DesktopSalesReviewEmailJob(
    IServiceProvider serviceProvider,
    ILogger<DesktopSalesReviewEmailJob> logger) : IJob
{
    public const string JobName = "desktop-sales-review-email";

    public async Task Execute(IJobExecutionContext context)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        foreach (var cadence in new[] { DesktopSalesReviewCadence.Weekly, DesktopSalesReviewCadence.Monthly })
        {
            try
            {
                var result = await mediator.Send(
                    new SendDesktopSalesReviewEmailCommand(cadence, Scheduled: true),
                    context.CancellationToken);

                if (result.IsError)
                {
                    logger.LogWarning("Desktop sales review {Cadence} email failed: {Error}", cadence, result.FirstError.Description);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One cadence failing must not stop the other; tomorrow's run catches this one up.
                logger.LogError(ex, "Desktop sales review {Cadence} email pass failed", cadence);
            }
        }
    }
}
