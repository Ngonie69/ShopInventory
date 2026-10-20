using MediatR;
using Quartz;
using ShopInventory.Features.InventoryTransfers.Commands.AnnounceTransferArrivals;

namespace ShopInventory.Services;

/// <summary>
/// Every two minutes: tells tills about the stock transfers SAP has booked into their warehouse. See
/// <see cref="AnnounceTransferArrivalsHandler"/>.
/// </summary>
/// <remarks>
/// Two minutes because that is how often TransferEventListener polls SAP for the same documents to move
/// the shop's ledger, so the alert lands about when the stock shows on the till. A pass is one filtered
/// SAP read and one indexed query.
/// </remarks>
[DisallowConcurrentExecution]
public sealed class TransferArrivalAlertJob(
    IServiceScopeFactory scopeFactory,
    ILogger<TransferArrivalAlertJob> logger) : IJob
{
    public const string JobName = "transfer-arrival-alert";

    public async Task Execute(IJobExecutionContext context)
    {
        using var scope = scopeFactory.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        try
        {
            var result = await sender.Send(new AnnounceTransferArrivalsCommand(DateTime.UtcNow), context.CancellationToken);

            if (result.IsError)
            {
                logger.LogWarning("Transfer arrival alerts were not sent this pass: {Error}", result.FirstError.Description);
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Nothing is recorded until it is about to be sent, so the next pass is the retry.
            logger.LogError(ex, "Transfer arrival alert pass failed.");
        }
    }
}
