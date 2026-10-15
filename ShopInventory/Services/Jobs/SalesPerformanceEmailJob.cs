using MediatR;
using Quartz;
using ShopInventory.Features.ManagementSales.Commands.SendSalesPerformanceEmail;
using ShopInventory.Features.ManagementSales.Queries.GetSalesPerformanceEmailSchedule;

namespace ShopInventory.Services;

/// <summary>
/// Emails management the sales performance of shops, vending and the vans: yesterday every morning, the
/// week just ended on Mondays, the month just ended on the 1st — each at 07:30 CAT when switched on.
/// </summary>
/// <remarks>
/// Runs every morning and asks for all three, rather than firing only on the right days. The send
/// remembers which period it last mailed, so a cadence with nothing due does nothing — and a Monday the
/// server was down is caught up the next morning. 07:30, not 07:00, to stay clear of the morning van
/// stock read and the Shop &amp; Vending review, which both run at 07:00.
/// </remarks>
[DisallowConcurrentExecution]
public sealed class SalesPerformanceEmailJob(
    IServiceProvider serviceProvider,
    ILogger<SalesPerformanceEmailJob> logger) : IJob
{
    public const string JobName = "sales-performance-email";

    public async Task Execute(IJobExecutionContext context)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        foreach (var cadence in SalesPerformanceEmailCadence.Scheduled)
        {
            try
            {
                var result = await mediator.Send(
                    new SendSalesPerformanceEmailCommand(cadence, Scheduled: true),
                    context.CancellationToken);

                if (result.IsError)
                {
                    logger.LogWarning("Sales performance {Cadence} email failed: {Error}", cadence, result.FirstError.Description);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One cadence failing must not stop the others; tomorrow's run catches this one up.
                logger.LogError(ex, "Sales performance {Cadence} email pass failed", cadence);
            }
        }
    }
}
