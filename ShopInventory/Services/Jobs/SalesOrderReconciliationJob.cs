using Microsoft.Extensions.DependencyInjection;
using Quartz;

namespace ShopInventory.Services;

/// <summary>
/// Quartz job that links local sales orders back to SAP documents that were created but never
/// recorded locally. A SAP create can commit while the response, the local save, or the short
/// in-request reconciliation window fails, which leaves the order showing Pending with no
/// document number until somebody re-approves it by hand. Scheduling, clustering and misfire
/// handling are owned by Quartz (see QuartzConfiguration).
/// </summary>
[DisallowConcurrentExecution]
public sealed class SalesOrderReconciliationJob : IJob
{
    private static readonly TimeSpan Lookback = TimeSpan.FromDays(7);
    private const int MaxOrdersPerRun = 25;

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SalesOrderReconciliationJob> _logger;

    public SalesOrderReconciliationJob(
        IServiceProvider serviceProvider,
        ILogger<SalesOrderReconciliationJob> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        using var scope = _serviceProvider.CreateScope();
        var salesOrderService = scope.ServiceProvider.GetRequiredService<ISalesOrderService>();

        try
        {
            var linkedCount = await salesOrderService.ReconcileUnlinkedSapSalesOrdersAsync(
                Lookback,
                MaxOrdersPerRun,
                context.CancellationToken);

            if (linkedCount > 0)
            {
                _logger.LogInformation(
                    "Sales order reconciliation linked {LinkedCount} local order(s) to their existing SAP documents",
                    linkedCount);
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never let a SAP outage fault the trigger; the next run retries the same candidates.
            _logger.LogError(ex, "Sales order SAP reconciliation sweep failed");
        }
    }
}
