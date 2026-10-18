using MediatR;
using Quartz;
using ShopInventory.Features.Invoices.Commands.RetryFailedInvoiceFiscalisations;

namespace ShopInventory.Services;

/// <summary>
/// Files again the SAP invoices whose fiscalisation failed. See <see cref="RetryFailedInvoiceFiscalisationsHandler"/>.
/// </summary>
/// <remarks>
/// <see cref="DisallowConcurrentExecutionAttribute"/> because two passes over one invoice would race to file it.
/// The platform would answer the second with the first receipt, but REVMax is asked before it files, and two
/// passes could both be told "nothing here" a moment apart.
/// </remarks>
[DisallowConcurrentExecution]
public sealed class InvoiceFiscalisationRetryJob(
    IServiceScopeFactory scopeFactory,
    ILogger<InvoiceFiscalisationRetryJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        using var scope = scopeFactory.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        try
        {
            var result = await sender.Send(new RetryFailedInvoiceFiscalisationsCommand(), context.CancellationToken);

            if (result.IsError)
            {
                logger.LogWarning("Invoice fiscalisation retry was refused: {Error}", result.FirstError.Description);
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // The next pass is the retry.
            logger.LogError(ex, "Invoice fiscalisation retry failed.");
        }
    }
}
