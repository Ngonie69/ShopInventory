using MediatR;
using Quartz;
using ShopInventory.Features.FiscalReceipts.Commands.PickUpFailedFiscalReceipts;

namespace ShopInventory.Services;

/// <summary>
/// Reads back the receipts of SAP invoices and credit notes whose fiscalisation failed. See
/// <see cref="PickUpFailedFiscalReceiptsHandler"/>.
/// </summary>
/// <remarks>
/// <see cref="DisallowConcurrentExecutionAttribute"/> only to keep the device's load to one look-up at a time: two
/// passes finding one receipt would write the same row, keyed on the document, twice.
/// </remarks>
[DisallowConcurrentExecution]
public sealed class FiscalReceiptPickupJob(
    IServiceScopeFactory scopeFactory,
    ILogger<FiscalReceiptPickupJob> logger) : IJob
{
    public const string JobName = "fiscal-receipt-pickup";

    public async Task Execute(IJobExecutionContext context)
    {
        using var scope = scopeFactory.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        try
        {
            var result = await sender.Send(new PickUpFailedFiscalReceiptsCommand(), context.CancellationToken);

            if (result.IsError)
            {
                logger.LogWarning("Fiscal receipt pick-up was refused: {Error}", result.FirstError.Description);
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // The next pass is the retry.
            logger.LogError(ex, "Fiscal receipt pick-up failed.");
        }
    }
}
