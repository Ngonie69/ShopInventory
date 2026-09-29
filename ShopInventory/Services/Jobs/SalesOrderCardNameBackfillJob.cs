using MediatR;
using Quartz;
using ShopInventory.Features.SalesOrders.Commands.BackfillSalesOrderCardNames;

namespace ShopInventory.Services;

/// <summary>
/// Fills in the customer name on sales orders stored without one, once, a few minutes after the API
/// starts.
/// </summary>
/// <remarks>
/// It used to run inline in startup, before the node was marked ready: one SAP business-partner read
/// per customer code, in series, with every deploy and restart waiting on it, and before any of the
/// runtime SAP switches were consulted. As a clustered job it runs on one node, after readiness, and
/// not while SAP is being held back.
/// </remarks>
[DisallowConcurrentExecution]
public sealed class SalesOrderCardNameBackfillJob(
    IServiceScopeFactory scopeFactory,
    ILogger<SalesOrderCardNameBackfillJob> logger) : IJob
{
    public const string JobName = "sales-order-card-name-backfill";
    public const string StartupTriggerName = "sales-order-card-name-backfill-startup";
    public static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(3);

    public async Task Execute(IJobExecutionContext context)
    {
        using var scope = scopeFactory.CreateScope();

        if (scope.ServiceProvider.GetService<SapCircuitBreakerState>() is { } circuit
            && circuit.ShouldHoldBackWork(out var holdBackReason))
        {
            logger.LogInformation("Skipping the sales order customer-name backfill: {Reason}.", holdBackReason);
            return;
        }

        var result = await scope.ServiceProvider.GetRequiredService<IMediator>()
            .Send(new BackfillSalesOrderCardNamesCommand(), context.CancellationToken);

        if (result.IsError)
        {
            logger.LogWarning(
                "Sales order customer-name backfill failed: {Errors}",
                string.Join("; ", result.Errors.Select(error => error.Description)));
        }
    }
}
