using Quartz;

namespace ShopInventory.Services;

/// <summary>
/// Posts each business partner's one incoming payment for the day at 17:00 CAT, and comes back for any
/// that did not post.
/// </summary>
/// <remarks>
/// The 17:00 trigger and the retry trigger hang off one job key. <see cref="DisallowConcurrentExecutionAttribute"/>
/// is enforced per key, and two passes running together could both find a customer without a payment
/// and both send one.
/// </remarks>
[DisallowConcurrentExecution]
public sealed class DailyIncomingPaymentJob(
    IServiceProvider serviceProvider,
    ILogger<DailyIncomingPaymentJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        using var scope = serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<DailyIncomingPaymentService>();

        try
        {
            var result = await service.RunAsync(DateTime.UtcNow, context.CancellationToken);

            if (result.Failed > 0 || result.Unresolved > 0)
            {
                logger.LogWarning(
                    "Daily incoming payments for {PaymentDate:yyyy-MM-dd}: {Failed} failed, {Unresolved} unresolved. {Errors}",
                    result.PaymentDate,
                    result.Failed,
                    result.Unresolved,
                    string.Join(" | ", result.Errors.Take(10)));
            }
        }
        catch (Exception ex)
        {
            // The retry trigger is the right retry. Rethrowing would hand it to Quartz's misfire policy.
            logger.LogError(ex, "Daily incoming payment pass failed.");
        }
    }
}
