using MediatR;
using Quartz;
using ShopInventory.Features.DailySalesWhatsApp.Commands.SendDailySalesWhatsApp;

namespace ShopInventory.Services;

/// <summary>
/// Sends yesterday's van and shop sales on WhatsApp to the saved numbers, when switched on in Settings.
/// </summary>
/// <remarks>
/// Fires every quarter hour from 07:15 to 09:45 CAT rather than once. The send remembers who already has
/// the day's report, so a pass with nobody left to send to does nothing — and a gateway that is down at
/// 07:15, or a pass that lands on a node with no WhatsApp settings, is caught up by a later pass. 07:15 is
/// after the morning van stock read and the Shop &amp; Vending review at 07:00, and reads only Postgres.
/// </remarks>
[DisallowConcurrentExecution]
public sealed class DailySalesWhatsAppJob(
    IServiceProvider serviceProvider,
    ILogger<DailySalesWhatsAppJob> logger) : IJob
{
    public const string JobName = "daily-sales-whatsapp";

    /// <summary>07:15, 07:30, 07:45, 08:15 … 09:45.</summary>
    public const string Schedule = "0 15/15 7-9 * * ?";

    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            await using var scope = serviceProvider.CreateAsyncScope();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

            var result = await mediator.Send(new SendDailySalesWhatsAppCommand(Scheduled: true), context.CancellationToken);
            if (result.IsError)
            {
                logger.LogWarning("Daily sales WhatsApp report failed: {Error}", result.FirstError.Description);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The next quarter hour tries again.
            logger.LogError(ex, "Daily sales WhatsApp report pass failed");
        }
    }
}
