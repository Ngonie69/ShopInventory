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
        var creditNoteSync = configuration.GetSection(CreditNoteSyncSettings.SectionName)
            .Get<CreditNoteSyncSettings>() ?? new CreditNoteSyncSettings();
        var dailyStock = configuration.GetSection("DailyStock").Get<DailyStockSettings>() ?? new DailyStockSettings();
        var creditLimit = configuration.GetSection(CreditLimitSettings.SectionName)
            .Get<CreditLimitSettings>() ?? new CreditLimitSettings();
        var healthAlert = configuration.GetSection("SystemHealthAlert").Get<SystemHealthAlertSettings>() ?? new SystemHealthAlertSettings();
        var lowStockAlert = configuration.GetSection(LowStockAlertSettings.SectionName)
            .Get<LowStockAlertSettings>() ?? new LowStockAlertSettings();
        var vanSalesPosting = configuration.GetSection(VanSalesPostingSettings.SectionName)
            .Get<VanSalesPostingSettings>() ?? new VanSalesPostingSettings();
        var desktopSalePosting = configuration.GetSection(DesktopSalePostingSettings.SectionName)
            .Get<DesktopSalePostingSettings>() ?? new DesktopSalePostingSettings();

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

            // Receipts vans signed offline, on their way to ZIMRA. Frequent because the deadline is the
            // fiscal day's automatic close, not end of day — a receipt still queued when its day closes is
            // one that never reaches ZIMRA at all.
            AddIntervalJob<VanSalesSignedReceiptIngestJob>(
                q, "van-sales-signed-receipt-ingest", TimeSpan.FromMinutes(2), startDelay: TimeSpan.FromMinutes(1));
            AddIntervalJob<SalesOrderReconciliationJob>(q, "sales-order-reconciliation", TimeSpan.FromMinutes(2), startDelay: TimeSpan.FromMinutes(1));

            if (sap.Enabled && creditNoteSync.Enabled)
            {
                AddIntervalJob<CreditNoteProjectionSyncJob>(
                    q,
                    "credit-note-projection-sync",
                    TimeSpan.FromMinutes(Math.Max(1, creditNoteSync.PollIntervalMinutes)),
                    startDelay: TimeSpan.FromSeconds(45));
            }

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

            // Ahead of the morning approval run, and off-peak so its SAP queries do not compete
            // with anything a person is waiting on.
            AddCronJob<SapItemUomWarmJob>(q, "sap-item-uom-warm", "0 30 3 * * ?");

            if (dailyStock.EnableAutoStockFetch)
            {
                AddCronJob<DailyStockSnapshotJob>(q, "daily-stock-snapshot", BuildDailyCron(dailyStock.StockFetchTimeCAT, "07:00"));
            }

            if (dailyStock.EnableAutoConsolidation)
            {
                AddCronJob<EndOfDayConsolidationJob>(q, "end-of-day-consolidation", BuildDailyCron(dailyStock.EndOfDayTimeCAT, "18:00"));
            }

            // Van sales post one invoice per ZIMRA receipt rather than being consolidated, so they run as
            // their own job and are filtered out of the consolidation above. Two triggers on one job: the
            // main run, and a mop-up for vans that only regained signal afterwards. Re-running is safe —
            // each sale posts under its own U_Van_saleorder and SAP is asked for it first.
            if (vanSalesPosting.Enabled)
            {
                AddCronJob<VanSalesEndOfDayPostingJob>(
                    q, "van-sales-eod-posting", BuildDailyCron(vanSalesPosting.PostingTimeCAT, "18:00"));

                AddCronJob<VanSalesEndOfDayPostingJob>(
                    q, "van-sales-eod-mopup", BuildDailyCron(vanSalesPosting.MopUpTimeCAT, "19:30"));

                // Since van sales fiscalise on the handset and are held rather than posted on the
                // request, waiting for the evening would leave SAP knowing nothing about a trading day
                // until it was over. This posts through the day.
                //
                // Attached as a second trigger on the 18:00 job rather than as a job of its own, and the
                // distinction matters: DisallowConcurrentExecution is enforced per job key, so a separate
                // key could run alongside the evening pass. Ninety minutes apart that was theoretical;
                // every half hour it is not, and two runs posting one sale at once could both ask SAP for
                // the van_order, both be told it does not exist, and both create it.
                if (vanSalesPosting.IntervalMinutes > 0)
                {
                    AddIntervalTriggerForJob<VanSalesEndOfDayPostingJob>(
                        q,
                        "van-sales-eod-posting",
                        "van-sales-interval-posting",
                        TimeSpan.FromMinutes(vanSalesPosting.IntervalMinutes),
                        // Past the startup rush, and long enough after a restart that handsets have had a
                        // chance to upload before the first pass looks for work.
                        startDelay: TimeSpan.FromMinutes(5));
                }
            }

            // Shop till and vending sales post one invoice each, like van sales, and are filtered out of
            // the consolidation above by the same source-system test. The interval is much shorter than
            // the van one: a till fiscalises, prints and hands over the receipt while the sale exists
            // only here, so every minute it is not in SAP is a minute the two disagree about what the
            // shop has sold.
            //
            // Registered whenever SAP is on, with no flag of its own. A flag here would be a way to
            // strand money: the sources it claims are excluded from the consolidation unconditionally,
            // so turning the job off would leave those sales fiscalised, refused by the 18:00 run and
            // picked up by nothing, silently. A source has exactly one route or it has none.
            if (sap.Enabled)
            {
                // Signs the sales that were stored unsigned — vending, which prints nothing and so has
                // no reason to hold a request open while the platform works. Registered next to the
                // posting job because it feeds it: the posting service only takes sales that have
                // fiscalised, so a vending sale this never reaches never reaches SAP either.
                //
                // Its own job key, unlike the two posting triggers, because it is a different piece of
                // work — and it must run whether or not a posting pass is in flight. AddIntervalJob
                // rather than AddIntervalTriggerForJob: the latter only adds a trigger, and one
                // pointing at a key no job was declared under never fires at all.
                if (desktopSalePosting.FiscalisationIntervalSeconds > 0)
                {
                    AddIntervalJob<DesktopSaleFiscalisationJob>(
                        q,
                        "desktop-sale-fiscalisation",
                        TimeSpan.FromSeconds(desktopSalePosting.FiscalisationIntervalSeconds),
                        startDelay: TimeSpan.FromMinutes(1));
                }

                AddCronJob<DesktopSalePostingJob>(
                    q, "desktop-sale-posting", BuildDailyCron(desktopSalePosting.SweepTimeCAT, "20:00"));

                if (desktopSalePosting.IntervalSeconds > 0)
                {
                    // Second trigger on the same job key, for the reason spelled out above: the guard
                    // against posting one sale twice is DisallowConcurrentExecution, and Quartz enforces
                    // it per key. A key of its own would let the interval pass and the evening sweep run
                    // together, both ask SAP for the same reference, both be told it does not exist, and
                    // both create it.
                    AddIntervalTriggerForJob<DesktopSalePostingJob>(
                        q,
                        "desktop-sale-posting",
                        "desktop-sale-interval-posting",
                        TimeSpan.FromSeconds(desktopSalePosting.IntervalSeconds),
                        startDelay: TimeSpan.FromMinutes(2));
                }
            }

            // After the daily stock snapshot, so it measures the figures the day is starting from.
            if (sap.Enabled && lowStockAlert.Enabled)
            {
                AddCronJob<LowStockReviewJob>(q, "low-stock-review", BuildDailyCron(lowStockAlert.ReviewTimeCAT, "07:30"));
            }

            // After the day's invoicing and payments are in, so the balances it reports are the
            // ones tomorrow's orders will actually be measured against.
            if (sap.Enabled && creditLimit.Enabled && creditLimit.EveningReviewEnabled)
            {
                AddCronJob<CreditLimitReviewJob>(q, "credit-limit-review", BuildDailyCron(creditLimit.ReviewTimeCAT, "19:15"));
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

    /// <summary>
    /// Adds a repeating trigger to a job that has already been declared elsewhere in this method.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="AddIntervalJob{TJob}"/> this does not declare the job, because declaring it
    /// twice under one key is what it is avoiding. Quartz enforces
    /// <see cref="DisallowConcurrentExecutionAttribute"/> per job key, so two schedules that must never
    /// overlap have to hang off the same key — which is the whole reason this helper exists.
    ///
    /// The caller is responsible for having registered <paramref name="jobName"/> first; a trigger
    /// pointing at a key no job was declared under simply never fires.
    /// </remarks>
    private static void AddIntervalTriggerForJob<TJob>(
        IServiceCollectionQuartzConfigurator quartz,
        string jobName,
        string triggerName,
        TimeSpan interval,
        TimeSpan? startDelay = null)
        where TJob : IJob
    {
        var jobKey = new JobKey(jobName);

        quartz.AddTrigger(trigger =>
        {
            trigger.ForJob(jobKey)
                .WithIdentity($"{triggerName}-trigger")
                .WithSimpleSchedule(schedule => schedule
                    .WithInterval(interval)
                    .RepeatForever()
                    // A run that overruns its slot skips the missed firings rather than queueing them:
                    // catching up would stack passes over the same sales to no purpose.
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
