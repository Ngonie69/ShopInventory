using MediatR;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Services;

/// <summary>
/// Drains <see cref="IInvoiceFiscalStatusBackfillQueue"/>, reading each invoice's fiscal status back
/// from the fiscalisation platform and recording it locally.
/// </summary>
/// <remarks>
/// Deliberately one at a time. A status lookup takes seconds and the platform is shared, so this
/// keeps exactly the request rate the old in-request backfill produced — the change is only that
/// nobody is waiting on it.
/// </remarks>
public sealed class InvoiceFiscalStatusBackfillService(
    IInvoiceFiscalStatusBackfillQueue queue,
    IServiceScopeFactory serviceScopeFactory,
    ILogger<InvoiceFiscalStatusBackfillService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Invoice fiscal status backfill service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            DTOs.InvoiceDto invoice;

            try
            {
                invoice = await queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                using var scope = serviceScopeFactory.CreateScope();
                var fiscalisationClient = scope.ServiceProvider.GetRequiredService<IFiscalisationApiClient>();
                var fiscalConfigCache = scope.ServiceProvider.GetRequiredService<IFiscalDeviceConfigCache>();
                var sender = scope.ServiceProvider.GetRequiredService<ISender>();

                await InvoiceFiscalTransactionSync.SyncAsync(
                    invoice, fiscalisationClient, fiscalConfigCache, sender, logger, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let one invoice stop the drain; the status simply stays unknown and the
                // next page view that sees it will offer it again.
                logger.LogWarning(ex, "Fiscal status backfill failed for invoice {DocNum}", invoice.DocNum);
            }
            finally
            {
                queue.Release(invoice.DocNum);
            }
        }

        logger.LogInformation("Invoice fiscal status backfill service stopped");
    }
}
