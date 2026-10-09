using MediatR;
using Quartz;
using ShopInventory.Features.CustomerDocuments.Commands.ScanNewInvoicesForDelivery;

namespace ShopInventory.Services;

/// <summary>
/// Every two minutes, asks SAP for the invoices posted since the last look and queues an automatic
/// WhatsApp send for each customer who asked for theirs.
/// </summary>
/// <remarks>
/// Declared when <c>CustomerDocuments:Enabled</c> and <c>SAP:Enabled</c> are both on in
/// appsettings.json — never from a node's own web.config, for the reason the delivery job gives. It
/// needs no gateway: it only writes rows, and the delivery job sends them from a node that can. Its SAP
/// read is one narrow page of headers at background priority, and it makes none while SAP is held back.
/// </remarks>
[DisallowConcurrentExecution]
public sealed class CustomerInvoiceScanJob(
    IServiceScopeFactory scopeFactory,
    ILogger<CustomerInvoiceScanJob> logger) : IJob
{
    public const string JobName = "customer-invoice-scan";

    public async Task Execute(IJobExecutionContext context)
    {
        using var scope = scopeFactory.CreateScope();

        var sapHeldBack = false;
        if (scope.ServiceProvider.GetService<SapCircuitBreakerState>() is { } circuit
            && circuit.ShouldHoldBackWork(out var holdBackReason))
        {
            sapHeldBack = true;
            logger.LogDebug("Invoice scan waits for SAP: {Reason}.", holdBackReason);
        }

        try
        {
            var result = await scope.ServiceProvider.GetRequiredService<IMediator>()
                .Send(new ScanNewInvoicesForDeliveryCommand(sapHeldBack), context.CancellationToken);

            if (result.IsError)
            {
                logger.LogWarning(
                    "Invoice scan failed: {Errors}",
                    string.Join("; ", result.Errors.Select(error => error.Description)));
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // SAP slow or refusing: the watermark has not moved, so the next pass reads the same invoices.
            logger.LogWarning(ex, "Invoice scan pass failed; the next pass starts from the same invoice");
        }
    }
}
