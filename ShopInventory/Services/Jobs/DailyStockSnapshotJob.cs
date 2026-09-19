using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Quartz;
using ShopInventory.Configuration;
using ShopInventory.Common.Stock;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Commands.FetchDailyStock;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// Quartz job that fetches daily stock snapshots from SAP for each monitored warehouse.
/// Enablement (DailyStock:EnableAutoStockFetch) and the run time (DailyStock:StockFetchTimeCAT)
/// are applied via the cron trigger in QuartzConfiguration; the job performs one fetch per fire.
/// </summary>
/// <remarks>
/// <para><b>Two triggers.</b> The morning cron fetches every monitored warehouse. The startup trigger,
/// a one-off shortly after every start, fetches only the warehouses whose snapshot for the day in force
/// is not finished.</para>
///
/// <para><b>Why the startup trigger exists.</b> Nothing else ever finished a fetch the morning run did
/// not. On 2026-09-17 the API restarted at 07:01, part-way through the 07:00 run: KEFSHOP was left
/// Pending, the other warehouses were never started, and nothing retried until someone fetched by hand
/// after 08:00. A start while the API was down at 07:00 is the same gap by another route — the cron
/// trigger's misfire policy is to do nothing, so that morning's run simply never happens.</para>
///
/// <para><b>Why it asks the data rather than Quartz.</b> Quartz's recovery can re-run a job a dead node
/// was executing, when the job asks for it, but not a fire it skipped while nothing was running, and
/// neither covers a fetch that ended Failed. Whether a warehouse has a finished snapshot answers all three the same way, and a start on a
/// normal day costs one query and fetches nothing.</para>
///
/// <para>A Pending snapshot is re-fetched on the assumption that nothing is still building it. Inside
/// this process that holds — see <see cref="StockFetchGate"/> — and across the cluster the job is
/// <see cref="DisallowConcurrentExecutionAttribute"/>, so this waits for any run on another node.</para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class DailyStockSnapshotJob : IJob
{
    /// <summary>The Quartz job key both triggers fire.</summary>
    public const string JobName = "daily-stock-snapshot";

    /// <summary>The one-off trigger added on every start. See the remarks on this class.</summary>
    public const string StartupTriggerName = "daily-stock-snapshot-startup-trigger";

    /// <summary>
    /// How long after start the catch-up runs: long enough to stay out of the way of startup itself.
    /// A till that cannot sell is waiting on it, so not much longer.
    /// </summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The repeating trigger that re-reads the unbatched half of a finished snapshot missing it. Its
    /// Quartz identity carries the <c>-trigger</c> suffix every interval trigger gets.
    /// </summary>
    public const string UnbatchedRetryTriggerName = "daily-stock-snapshot-unbatched-retry";

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DailyStockSnapshotJob> _logger;
    private readonly DailyStockSettings _settings;

    public DailyStockSnapshotJob(
        IServiceProvider serviceProvider,
        IOptions<DailyStockSettings> settings,
        ILogger<DailyStockSnapshotJob> logger)
    {
        _serviceProvider = serviceProvider;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        using var scope = _serviceProvider.CreateScope();

        // The day the fetch is producing figures for, resolved the same way every reader of them
        // resolves it. Computed here rather than taken as "today" so a run that starts a minute
        // either side of 07:00 still stamps the day it is fetching for.
        var today = StockLedgerDay.Today(_settings.StockFetchTimeCAT);
        var warehouses = _settings.MonitoredWarehouses;

        if (context.Trigger.Key.Name == $"{UnbatchedRetryTriggerName}-trigger")
        {
            await RetryUnbatchedAsync(scope, warehouses, today, context.CancellationToken);
            return;
        }

        if (context.Trigger.Key.Name == StartupTriggerName)
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var unfinished = await UnfinishedAsync(db, warehouses, today, context.CancellationToken);

            if (unfinished.Count == 0)
            {
                _logger.LogInformation(
                    "Stock snapshot check after start: all {Count} monitored warehouse(s) have a finished snapshot for {Day:yyyy-MM-dd}",
                    warehouses.Count, today);
                return;
            }

            // Warning, because every one of these is a warehouse whose tills were refused, or were
            // selling from yesterday's figures, until now.
            _logger.LogWarning(
                "Stock snapshot check after start: {Count} warehouse(s) have no finished snapshot for {Day:yyyy-MM-dd}, fetching them now: {Warehouses}",
                unfinished.Count,
                today,
                string.Join(", ", unfinished.Select(entry => $"{entry.WarehouseCode} ({entry.Status?.ToString() ?? "none"})")));

            warehouses = unfinished.Select(entry => entry.WarehouseCode).ToList();
        }

        await RunStockFetchAsync(scope, warehouses, today, context.CancellationToken);
    }

    /// <summary>
    /// The warehouses, in the order given, without a finished snapshot for <paramref name="day"/>, and
    /// how far each got: Pending, Failed, or null for no snapshot at all.
    /// </summary>
    internal static async Task<List<(string WarehouseCode, StockSnapshotStatus? Status)>> UnfinishedAsync(
        ApplicationDbContext db,
        IReadOnlyCollection<string> warehouses,
        DateTime day,
        CancellationToken cancellationToken)
    {
        var codes = warehouses.ToList();

        var statuses = await db.DailyStockSnapshots
            .AsNoTracking()
            .Where(snapshot => snapshot.SnapshotDate == day && codes.Contains(snapshot.WarehouseCode))
            .Select(snapshot => new { snapshot.WarehouseCode, snapshot.Status })
            .ToListAsync(cancellationToken);

        return codes
            .Select(code => (
                WarehouseCode: code,
                Status: statuses
                    .Where(entry => string.Equals(entry.WarehouseCode, code, StringComparison.OrdinalIgnoreCase))
                    .Select(entry => (StockSnapshotStatus?)entry.Status)
                    .FirstOrDefault()))
            .Where(entry => entry.Status != StockSnapshotStatus.Complete)
            .ToList();
    }

    /// <summary>
    /// The warehouses, in the order given, whose snapshot for <paramref name="day"/> is finished but
    /// is missing its unbatched half.
    /// </summary>
    internal static async Task<List<string>> MissingUnbatchedAsync(
        ApplicationDbContext db,
        IReadOnlyCollection<string> warehouses,
        DateTime day,
        CancellationToken cancellationToken)
    {
        var codes = warehouses.ToList();

        var flagged = await db.DailyStockSnapshots
            .AsNoTracking()
            .Where(snapshot => snapshot.SnapshotDate == day
                            && codes.Contains(snapshot.WarehouseCode)
                            && snapshot.Status == StockSnapshotStatus.Complete
                            && snapshot.UnbatchedStockMissing)
            .Select(snapshot => snapshot.WarehouseCode)
            .ToListAsync(cancellationToken);

        return codes
            .Where(code => flagged.Contains(code, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Re-reads the unbatched half of every snapshot for the day that finished without it.
    /// </summary>
    /// <remarks>
    /// On 2026-09-19 the morning fetch read the batch-managed items and then failed to read the
    /// unbatched ones. The snapshots were finished with the flag set, and nothing read that half
    /// again: the morning run's retry covers only fetches that threw, and the startup run covers
    /// only unfinished snapshots. The shops sold without their unbatched items until someone
    /// fetched by hand. <see cref="FetchDailyStockHandler"/> reads only the missing half of a
    /// flagged snapshot, so the rows the tills are already selling from are not touched.
    /// </remarks>
    private async Task RetryUnbatchedAsync(
        IServiceScope scope,
        IReadOnlyCollection<string> warehouses,
        DateTime today,
        CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var missing = await MissingUnbatchedAsync(db, warehouses, today, ct);

        if (missing.Count == 0)
        {
            return;
        }

        _logger.LogWarning(
            "{Count} warehouse(s) are missing unbatched stock from their {Day:yyyy-MM-dd} snapshot, reading it again: {Warehouses}",
            missing.Count, today, string.Join(", ", missing));

        var handler = scope.ServiceProvider.GetRequiredService<FetchDailyStockHandler>();

        foreach (var warehouse in missing)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await handler.FetchWarehouseStockAsync(today, warehouse, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Retrying unbatched stock for warehouse {Warehouse} failed", warehouse);
            }
        }
    }

    private async Task RunStockFetchAsync(
        IServiceScope scope,
        IReadOnlyCollection<string> warehouses,
        DateTime today,
        CancellationToken ct)
    {
        _logger.LogInformation("Starting daily stock snapshot fetch for {Count} warehouses",
            warehouses.Count);

        var handler = scope.ServiceProvider.GetRequiredService<FetchDailyStockHandler>();
        var failedWarehouses = new List<string>();

        foreach (var warehouse in warehouses)
        {
            try
            {
                await handler.FetchWarehouseStockAsync(today, warehouse, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch stock for warehouse {Warehouse}", warehouse);
                failedWarehouses.Add(warehouse);
            }
        }

        // Retry failed warehouses once
        foreach (var warehouse in failedWarehouses)
        {
            try
            {
                _logger.LogInformation("Retrying stock fetch for warehouse {Warehouse}", warehouse);
                await handler.FetchWarehouseStockAsync(today, warehouse, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Retry failed for warehouse {Warehouse}", warehouse);
            }
        }

        _logger.LogInformation("Daily stock snapshot fetch complete");
    }
}
