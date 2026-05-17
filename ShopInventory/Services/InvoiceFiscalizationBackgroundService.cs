using ShopInventory.Features.Invoices.Events;

namespace ShopInventory.Services;

public sealed class InvoiceFiscalizationBackgroundService(
    IInvoiceFiscalizationQueue queue,
    IServiceScopeFactory serviceScopeFactory,
    ILogger<InvoiceFiscalizationBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Invoice fiscalization background service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            InvoiceFiscalizationWorkItem workItem;

            try
            {
                workItem = await queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            await ProcessAsync(workItem, stoppingToken);
        }

        logger.LogInformation("Invoice fiscalization background service stopped");
    }

    private async Task ProcessAsync(InvoiceFiscalizationWorkItem workItem, CancellationToken stoppingToken)
    {
        try
        {
            using var scope = serviceScopeFactory.CreateScope();
            var fiscalizationService = scope.ServiceProvider.GetRequiredService<IFiscalizationService>();

            var result = await fiscalizationService.FiscalizeInvoiceAsync(
                workItem.Invoice,
                workItem.CustomerDetails,
                stoppingToken);

            if (result.Success)
            {
                logger.LogInformation(
                    "Invoice {DocNum} fiscalized in background. QRCode: {HasQR}, ReceiptGlobalNo: {ReceiptNo}",
                    workItem.Invoice.DocNum,
                    !string.IsNullOrEmpty(result.QRCode),
                    result.ReceiptGlobalNo);
            }
            else
            {
                logger.LogWarning(
                    "Background fiscalization failed for invoice {DocNum}: {Message}",
                    workItem.Invoice.DocNum,
                    result.Message);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Background fiscalization cancelled for invoice {DocNum}", workItem.Invoice.DocNum);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Background fiscalization error for invoice {DocNum}", workItem.Invoice.DocNum);
        }
    }
}