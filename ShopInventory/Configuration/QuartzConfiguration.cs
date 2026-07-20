using Quartz;
using ShopInventory.Services;

namespace ShopInventory.Configuration;

/// <summary>
/// Registers the API's background jobs on a clustered, Postgres-backed Quartz scheduler.
/// Replaces the hand-rolled BackgroundService + advisory-lock leader-election workers:
/// Quartz owns scheduling, clustering (one node runs each job at a time), persistence,
/// and misfire handling. Two in-memory-queue consumers stay as plain hosted services
/// (InvoiceFiscalizationBackgroundService, and the Web app's CachePreloadService) because
/// they process per-process channels that cannot be clustered.
/// </summary>
public static class QuartzConfiguration
{
    private const string SchedulerName = "ShopInventoryApi";

    private static readonly TimeZoneInfo CatTimeZone = ResolveCatTimeZone();

    public static IServiceCollection AddShopInventoryQuartz(
        this IServiceCollection services,
        IConfiguration configuration,
        string connectionString)
    {
        var sap = configuration.GetSection("SAP").Get<SAPSettings>() ?? new SAPSettings();
        var dailyStock = configuration.GetSection("DailyStock").Get<DailyStockSettings>() ?? new DailyStockSettings();
        var healthAlert = configuration.GetSection("SystemHealthAlert").Get<SystemHealthAlertSettings>() ?? new SystemHealthAlertSettings();

        services.AddQuartz(q =>
        {
            q.SchedulerName = SchedulerName;
            q.SchedulerId = "AUTO"; // unique instance id per node — required for clustering

            q.UsePersistentStore(store =>
            {
                store.UseProperties = true;
                store.UsePostgres(postgres => postgres.ConnectionString = connectionString);
                store.UseSystemTextJsonSerializer();
                store.UseClustering(cluster =>
                {
                    cluster.CheckinInterval = TimeSpan.FromSeconds(15);
                    cluster.CheckinMisfireThreshold = TimeSpan.FromSeconds(20);
                });
            });

            // DB-queue pollers and periodic maintenance → interval triggers (cadence preserved).
            AddIntervalJob<MobileOrderPostProcessingJob>(q, "mobile-order-post-processing", TimeSpan.FromSeconds(5));
            AddIntervalJob<InvoicePostingJob>(q, "invoice-posting", TimeSpan.FromSeconds(10));
            AddIntervalJob<InventoryTransferPostingJob>(q, "inventory-transfer-posting", TimeSpan.FromSeconds(10));
            AddIntervalJob<IncomingPaymentPostingJob>(q, "incoming-payment-posting", TimeSpan.FromSeconds(10));
            AddIntervalJob<ReservationCleanupJob>(q, "reservation-cleanup", TimeSpan.FromMinutes(1), startDelay: TimeSpan.FromSeconds(30));
            AddIntervalJob<SalesOrderReconciliationJob>(q, "sales-order-reconciliation", TimeSpan.FromMinutes(2), startDelay: TimeSpan.FromMinutes(1));

            if (sap.AutoSyncEnabled)
            {
                AddIntervalJob<PriceCatalogSyncJob>(q, "price-catalog-sync",
                    TimeSpan.FromHours(Math.Max(1, sap.SyncIntervalHours)),
                    startDelay: TimeSpan.FromMinutes(Math.Max(0, sap.InitialDelayMinutes)));
            }

            if (healthAlert.Enabled)
            {
                AddIntervalJob<SystemFailureAlertJob>(q, "system-failure-alert",
                    TimeSpan.FromMinutes(Math.Max(1, healthAlert.CheckIntervalMinutes)),
                    startDelay: TimeSpan.FromSeconds(30));
            }

            // Time-of-day jobs → cron triggers evaluated in CAT.
            if (dailyStock.EnableAutoStockFetch)
            {
                AddCronJob<DailyStockSnapshotJob>(q, "daily-stock-snapshot", BuildDailyCron(dailyStock.StockFetchTimeCAT, "07:00"));
            }

            if (dailyStock.EnableAutoConsolidation)
            {
                AddCronJob<EndOfDayConsolidationJob>(q, "end-of-day-consolidation", BuildDailyCron(dailyStock.EndOfDayTimeCAT, "18:00"));
            }
        });

        services.AddQuartzHostedService(options =>
        {
            options.WaitForJobsToComplete = true;
            options.AwaitApplicationStarted = true;
        });

        return services;
    }

    private static void AddIntervalJob<TJob>(
        IServiceCollectionQuartzConfigurator quartz,
        string name,
        TimeSpan interval,
        TimeSpan? startDelay = null)
        where TJob : IJob
    {
        var jobKey = new JobKey(name);
        quartz.AddJob<TJob>(job => job.WithIdentity(jobKey).StoreDurably());
        quartz.AddTrigger(trigger =>
        {
            trigger.ForJob(jobKey)
                .WithIdentity($"{name}-trigger")
                .WithSimpleSchedule(schedule => schedule
                    .WithInterval(interval)
                    .RepeatForever()
                    .WithMisfireHandlingInstructionNextWithRemainingCount());

            if (startDelay is { } delay && delay > TimeSpan.Zero)
            {
                trigger.StartAt(DateTimeOffset.UtcNow.Add(delay));
            }
            else
            {
                trigger.StartNow();
            }
        });
    }

    private static void AddCronJob<TJob>(
        IServiceCollectionQuartzConfigurator quartz,
        string name,
        string cronExpression)
        where TJob : IJob
    {
        var jobKey = new JobKey(name);
        quartz.AddJob<TJob>(job => job.WithIdentity(jobKey).StoreDurably());
        quartz.AddTrigger(trigger => trigger
            .ForJob(jobKey)
            .WithIdentity($"{name}-trigger")
            .WithCronSchedule(cronExpression, cron => cron
                .InTimeZone(CatTimeZone)
                .WithMisfireHandlingInstructionDoNothing()));
    }

    private static string BuildDailyCron(string timeCat, string fallback)
    {
        if (!TimeSpan.TryParse(timeCat, out var time))
        {
            TimeSpan.TryParse(fallback, out time);
        }

        return $"0 {time.Minutes} {time.Hours} * * ?";
    }

    private static TimeZoneInfo ResolveCatTimeZone()
    {
        foreach (var id in new[] { "South Africa Standard Time", "Africa/Johannesburg", "Africa/Harare" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
                // Try the next identifier.
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone("CAT", TimeSpan.FromHours(2), "CAT", "CAT");
    }
}
