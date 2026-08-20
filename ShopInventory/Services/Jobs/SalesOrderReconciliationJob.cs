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

    /// <summary>
    /// The window every run covers. An order that SAP really did accept shows up within moments, so
    /// this is where a repair actually happens.
    /// </summary>
    private static readonly TimeSpan RecentLookback = TimeSpan.FromHours(2);

    /// <summary>How often the full <see cref="Lookback"/> is swept instead of just the recent window.</summary>
    private static readonly TimeSpan FullSweepInterval = TimeSpan.FromMinutes(30);

    /// <summary>The trigger interval, from QuartzConfiguration. Sets the width of the full-sweep slot.</summary>
    private static readonly TimeSpan TriggerInterval = TimeSpan.FromMinutes(2);

    private const int MaxOrdersPerRun = 25;

    /// <summary>
    /// Whether this run sweeps the whole lookback or only the recent window.
    /// </summary>
    /// <remarks>
    /// An order SAP never received does not become one it did. Probing the full seven days every two
    /// minutes asked SAP the same question 720 times a day and wrote 720 lines saying it got the same
    /// answer: on 2026-08-20, 276 sweeps in nine hours, all naming SO-20260817-0008, which had been
    /// unlinked since the 17th. Roughly two thousand futile probes for one order.
    /// <para>
    /// Bucketed on the scheduled fire time rather than counted in the job data map, so every node in
    /// the cluster agrees on which runs are full ones without sharing state.
    /// </para>
    /// </remarks>
    internal static bool IsFullSweep(DateTimeOffset scheduledFireTimeUtc, TimeSpan triggerInterval) =>
        scheduledFireTimeUtc.TimeOfDay.Ticks % FullSweepInterval.Ticks < triggerInterval.Ticks;

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

        var isFullSweep = IsFullSweep(
            context.ScheduledFireTimeUtc ?? context.FireTimeUtc,
            TriggerInterval);

        try
        {
            var linkedCount = await salesOrderService.ReconcileUnlinkedSapSalesOrdersAsync(
                isFullSweep ? Lookback : RecentLookback,
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
