using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Quartz;
using ShopInventory.Common.Stock;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.FetchDailyStock;
using ShopInventory.Hubs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the stock fetch that runs once after every start, for the morning run a restart interrupted.
/// </summary>
/// <remarks>
/// On 2026-09-17 the API restarted at 07:01, part-way through the 07:00 fetch. KEFSHOP's snapshot was
/// left Pending, the other warehouses were never started, and nothing retried until someone fetched by
/// hand after 08:00. See <see cref="DailyStockSnapshotJob"/>.
/// </remarks>
public sealed class DailyStockStartupCatchUpTests : IDisposable
{
    private const string Shop = "KEFSHOP";
    private const string Machine = "CORMACH";
    private const string Groceries = "KEFGRS";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly List<string> _sapReads = [];
    private readonly List<string> _unbatchedReads = [];

    private readonly DailyStockSettings _settings = new()
    {
        MonitoredWarehouses = [Shop, Machine, Groceries]
    };

    public DailyStockStartupCatchUpTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new SnapshotSqliteContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ── What the startup run fetches ────────────────────

    /// <summary>
    /// The morning of 2026-09-17, as the restarted API found it.
    /// </summary>
    [Fact]
    public async Task After_a_start_the_warehouses_without_a_finished_snapshot_are_fetched()
    {
        await SeedAsync(Shop, StockSnapshotStatus.Pending);
        await SeedAsync(Machine, StockSnapshotStatus.Complete);

        await RunAsync(DailyStockSnapshotJob.StartupTriggerName);

        Assert.Equal([Shop, Groceries], _sapReads);
        Assert.Equal(StockSnapshotStatus.Complete, await StatusAsync(Shop));
        Assert.Equal(StockSnapshotStatus.Complete, await StatusAsync(Groceries));
        Assert.Equal(2, await _context.DailyStockSnapshotItems.CountAsync(row => row.WarehouseCode == Shop));
    }

    [Fact]
    public async Task A_fetch_that_failed_before_the_restart_is_fetched_again()
    {
        await SeedAsync(Shop, StockSnapshotStatus.Failed);
        await SeedAsync(Machine, StockSnapshotStatus.Complete);
        await SeedAsync(Groceries, StockSnapshotStatus.Complete);

        await RunAsync(DailyStockSnapshotJob.StartupTriggerName);

        Assert.Equal([Shop], _sapReads);
        Assert.Equal(StockSnapshotStatus.Complete, await StatusAsync(Shop));
    }

    [Fact]
    public async Task On_a_normal_start_nothing_is_fetched()
    {
        await SeedAsync(Shop, StockSnapshotStatus.Complete);
        await SeedAsync(Machine, StockSnapshotStatus.Complete);
        await SeedAsync(Groceries, StockSnapshotStatus.Complete);

        await RunAsync(DailyStockSnapshotJob.StartupTriggerName);

        Assert.Empty(_sapReads);
    }

    /// <summary>
    /// A start is not a second morning run. The morning run re-reads the unbatched half of a finished
    /// snapshot that is missing it; the API restarts often enough that doing that on every start would
    /// be SAP load for nothing the tills are waiting on.
    /// </summary>
    [Fact]
    public async Task A_start_leaves_finished_snapshots_alone_even_ones_the_morning_run_would_top_up()
    {
        await SeedAsync(Shop, StockSnapshotStatus.Complete, unbatchedMissing: true);
        await SeedAsync(Machine, StockSnapshotStatus.Complete);
        await SeedAsync(Groceries, StockSnapshotStatus.Complete);

        await RunAsync(DailyStockSnapshotJob.StartupTriggerName);

        Assert.Empty(_sapReads);
        Assert.Empty(_unbatchedReads);
    }

    [Fact]
    public async Task The_morning_run_still_tops_up_a_finished_snapshot_missing_its_unbatched_half()
    {
        await SeedAsync(Shop, StockSnapshotStatus.Complete, unbatchedMissing: true);
        await SeedAsync(Machine, StockSnapshotStatus.Complete);
        await SeedAsync(Groceries, StockSnapshotStatus.Complete);

        await RunAsync($"{DailyStockSnapshotJob.JobName}-trigger");

        Assert.Equal([Shop], _unbatchedReads);
    }

    [Fact]
    public async Task Each_unfinished_warehouse_is_named_with_how_far_it_got()
    {
        await SeedAsync(Shop, StockSnapshotStatus.Pending);
        await SeedAsync(Machine, StockSnapshotStatus.Complete);

        var unfinished = await DailyStockSnapshotJob.UnfinishedAsync(
            _context, _settings.MonitoredWarehouses, Today, default);

        Assert.Equal([(Shop, (StockSnapshotStatus?)StockSnapshotStatus.Pending), (Groceries, null)], unfinished);
    }

    /// <summary>
    /// Only today counts. A finished snapshot from yesterday is not a finished snapshot for today.
    /// </summary>
    [Fact]
    public async Task A_finished_snapshot_for_another_day_does_not_count()
    {
        await SeedAsync(Shop, StockSnapshotStatus.Complete, Today.AddDays(-1));

        var unfinished = await DailyStockSnapshotJob.UnfinishedAsync(
            _context, [Shop], Today, default);

        Assert.Equal([(Shop, (StockSnapshotStatus?)null)], unfinished);
    }

    // ── The trigger ─────────────────────────────────────

    [Fact]
    public async Task The_startup_trigger_fires_its_job_once()
    {
        var fires = await FiresWithinAsync(TimeSpan.FromSeconds(1), watchFor: TimeSpan.FromSeconds(4));

        Assert.Equal([DailyStockSnapshotJob.StartupTriggerName], fires);
    }

    /// <summary>
    /// A start slow enough that the trigger's time has passed before the scheduler runs. The FireNow
    /// misfire policy has to fire it anyway, and — the trap a repeating trigger falls into — only once.
    /// </summary>
    [Fact]
    public async Task A_startup_trigger_whose_time_passed_before_the_scheduler_started_still_fires_once()
    {
        var fires = await FiresWithinAsync(TimeSpan.FromMinutes(-2), watchFor: TimeSpan.FromSeconds(3));

        Assert.Equal([DailyStockSnapshotJob.StartupTriggerName], fires);
    }

    /// <summary>
    /// The API's real registration declares the trigger, on the job the morning cron fires. Read from the
    /// options the scheduler is built from; no scheduler is started, so no database is needed.
    /// </summary>
    [Fact]
    public void The_api_declares_the_startup_trigger_beside_the_morning_run()
    {
        var triggers = DeclaredTriggers(enableAutoStockFetch: true);

        var startup = Assert.Single(triggers, trigger => trigger.Key.Name == DailyStockSnapshotJob.StartupTriggerName);
        Assert.Equal(new JobKey(DailyStockSnapshotJob.JobName), startup.JobKey);
        Assert.Equal(0, Assert.IsAssignableFrom<ISimpleTrigger>(startup).RepeatCount);
        Assert.True(startup.StartTimeUtc > DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.Contains(triggers, trigger => trigger.Key.Name == $"{DailyStockSnapshotJob.JobName}-trigger"
                                          && trigger.JobKey.Equals(startup.JobKey));
    }

    [Fact]
    public void With_the_morning_fetch_switched_off_there_is_no_startup_fetch_either()
    {
        var triggers = DeclaredTriggers(enableAutoStockFetch: false);

        Assert.DoesNotContain(triggers, trigger => trigger.Key.Name == DailyStockSnapshotJob.StartupTriggerName);
    }

    // ── Helpers ─────────────────────────────────────────

    private static IReadOnlyList<ITrigger> DeclaredTriggers(bool enableAutoStockFetch)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DailyStock:EnableAutoStockFetch"] = enableAutoStockFetch.ToString()
            })
            .Build();

        var services = new ServiceCollection();
        services.AddShopInventoryQuartz(configuration, "Host=unused");

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<QuartzOptions>>().Value.Triggers.ToList();
    }

    private DateTime Today => StockLedgerDay.Today(_settings.StockFetchTimeCAT);

    private async Task SeedAsync(
        string warehouse,
        StockSnapshotStatus status,
        DateTime? day = null,
        bool unbatchedMissing = false)
    {
        _context.DailyStockSnapshots.Add(new DailyStockSnapshotEntity
        {
            SnapshotDate = day ?? Today,
            WarehouseCode = warehouse,
            Status = status,
            UnbatchedStockMissing = unbatchedMissing,
            CreatedAt = DateTime.UtcNow
        });

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private async Task<StockSnapshotStatus> StatusAsync(string warehouse) =>
        (await _context.DailyStockSnapshots
            .AsNoTracking()
            .SingleAsync(snapshot => snapshot.WarehouseCode == warehouse && snapshot.SnapshotDate == Today))
        .Status;

    private async Task RunAsync(string triggerName)
    {
        var handler = new FetchDailyStockHandler(
            _context,
            Sap(),
            StubProxy.Unused<IHubContext<NotificationHub>>(),
            Options.Create(_settings),
            StubProxy.Unused<ITransferEventListenerClient>(),
            Options.Create(new TransferEventListenerSettings()),
            new StockFetchGate(),
            NullLogger<FetchDailyStockHandler>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton(_context);
        services.AddSingleton(handler);

        var job = new DailyStockSnapshotJob(
            services.BuildServiceProvider(),
            Options.Create(_settings),
            NullLogger<DailyStockSnapshotJob>.Instance);

        var trigger = TriggerBuilder.Create()
            .WithIdentity(triggerName)
            .ForJob(DailyStockSnapshotJob.JobName)
            .StartNow()
            .Build();

        await job.Execute(StubProxy.For<IJobExecutionContext>((method, _) => method.Name switch
        {
            "get_" + nameof(IJobExecutionContext.Trigger) => trigger,
            "get_" + nameof(IJobExecutionContext.CancellationToken) => CancellationToken.None,
            _ => throw new InvalidOperationException($"IJobExecutionContext.{method.Name} was not expected.")
        }));

        _context.ChangeTracker.Clear();
    }

    /// <summary>
    /// Two batches of one item in whichever warehouse is asked about, recording which were read.
    /// </summary>
    private ISAPServiceLayerClient Sap() =>
        StubProxy.For<ISAPServiceLayerClient>((method, args) =>
        {
            switch (method.Name)
            {
                case nameof(ISAPServiceLayerClient.GetAllBatchNumbersInWarehouseAsync):
                    var warehouse = (string)args![0]!;
                    _sapReads.Add(warehouse);
                    return Task.FromResult(new List<BatchNumber>
                    {
                        new() { ItemCode = "CHE011", ItemName = "Feta 1kg", BatchNum = "B-1", Quantity = 4m, ExpiryDate = "2026-10-01" },
                        new() { ItemCode = "CHE011", ItemName = "Feta 1kg", BatchNum = "B-2", Quantity = 5m, ExpiryDate = "2026-11-01" }
                    });

                case nameof(ISAPServiceLayerClient.GetStockQuantitiesInWarehouseAsync):
                    return Task.FromResult(new List<StockQuantityDto>
                    {
                        new() { ItemCode = "CHE011", ItemName = "Feta 1kg", WarehouseCode = (string)args![0]!, InStock = 9m }
                    });

                case nameof(ISAPServiceLayerClient.GetNonBatchStockQuantitiesInWarehouseAsync):
                    _unbatchedReads.Add((string)args![0]!);
                    return Task.FromResult(new List<StockQuantityDto>());

                default:
                    throw new InvalidOperationException($"Unexpected SAP call: {method.Name}");
            }
        });

    /// <summary>
    /// Runs a real, in-memory Quartz scheduler holding one job and the startup trigger exactly as
    /// <see cref="QuartzConfiguration"/> defines it, and returns the name of the trigger behind every
    /// fire seen in <paramref name="watchFor"/>.
    /// </summary>
    /// <remarks>
    /// Built with <see cref="SchedulerBuilder"/>, not <c>services.AddQuartz</c>. The container route
    /// installs Quartz's process-wide log provider over the container's logger factory, and disposing
    /// the container then breaks every other Quartz test running in the same process with
    /// "Cannot access a disposed object: LoggerFactory".
    /// </remarks>
    private static async Task<List<string>> FiresWithinAsync(TimeSpan delay, TimeSpan watchFor)
    {
        // Unique names: schedulers live in a process-wide repository, and fires are recorded statically.
        var jobName = $"{DailyStockSnapshotJob.JobName}-{Guid.NewGuid():N}";

        var scheduler = await SchedulerBuilder.Create()
            .WithId(Guid.NewGuid().ToString("N"))
            .WithName(Guid.NewGuid().ToString("N"))
            .UseDefaultThreadPool(pool => pool.MaxConcurrency = 1)
            .BuildScheduler();

        try
        {
            // Through QuartzOptions, the object the container route fills, so the trigger is built by the
            // same configurator production uses — without a container behind it.
            var options = new QuartzOptions();
            options.AddTrigger(configurator => QuartzConfiguration.ConfigureStartupTrigger(
                configurator, jobName, DailyStockSnapshotJob.StartupTriggerName, delay));
            var trigger = Assert.Single(options.Triggers);

            await scheduler.ScheduleJob(
                JobBuilder.Create<RecordingJob>().WithIdentity(jobName).StoreDurably().Build(),
                trigger);

            await scheduler.Start();
            await Task.Delay(watchFor);
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: true);
        }

        return RecordingJob.Fires
            .Where(fire => fire.Job == jobName)
            .Select(fire => fire.Trigger)
            .ToList();
    }

    private sealed class RecordingJob : IJob
    {
        public static System.Collections.Concurrent.ConcurrentQueue<(string Job, string Trigger)> Fires { get; } = new();

        public Task Execute(IJobExecutionContext context)
        {
            Fires.Enqueue((context.JobDetail.Key.Name, context.Trigger.Key.Name));
            return Task.CompletedTask;
        }
    }
}
