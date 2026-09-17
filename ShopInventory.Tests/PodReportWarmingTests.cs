using ErrorOr;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Quartz;
using ShopInventory.Common.Caching;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Invoices.Queries.GetPodUploadStatus;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers which POD reports get rebuilt off the request path, and when.
/// </summary>
/// <remarks>
/// A cache hit answers in 30–90 ms; on 2026-08-20 six misses landed in ninety minutes at 6 to 64
/// seconds each, every one paid for by somebody waiting. Snapshots live fifteen minutes and users
/// pick their own date ranges, so ordinary morning use straddles expiry repeatedly.
/// <para>
/// The trap in warming is doing too much of it: a fixed list of presets on a timer is several
/// hundred SAP rebuilds a day for reports nobody opened. These tests hold the property that makes it
/// pay — warming follows what was actually asked for, and stops when nobody is asking.
/// </para>
/// </remarks>
public sealed class PodReportWarmingTests : IDisposable
{
    private static readonly TimeSpan Freshness = TimeSpan.FromMinutes(15);
    private static readonly DateTime Now = new(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>The job's production interval, from <c>QuartzConfiguration</c>.</summary>
    private static readonly TimeSpan JobInterval = TimeSpan.FromMinutes(5);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public PodReportWarmingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
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

    private static PodReportWarmKey Shape(int days) =>
        new(new DateTime(2026, 8, 20).AddDays(-days), new DateTime(2026, 8, 20), "global");

    [Fact]
    public void Nothing_is_warmed_until_somebody_asks_for_something()
    {
        Assert.Empty(new PodReportWarmSet().ActiveShapes());
    }

    [Fact]
    public void A_requested_shape_becomes_a_warming_candidate()
    {
        var set = new PodReportWarmSet();
        set.Record(Shape(30));

        Assert.Equal([Shape(30)], set.ActiveShapes().Select(shape => shape.Key));
    }

    [Fact]
    public void Asking_for_the_same_shape_twice_tracks_it_once()
    {
        var set = new PodReportWarmSet();
        set.Record(Shape(30));
        set.Record(Shape(30));

        Assert.Single(set.ActiveShapes());
    }

    /// <summary>
    /// Two users on different ranges are two shapes; one user's range must not warm on the other's
    /// key or the snapshot saved would answer the wrong question.
    /// </summary>
    [Fact]
    public void Different_ranges_are_different_shapes()
    {
        var set = new PodReportWarmSet();
        set.Record(Shape(7));
        set.Record(Shape(30));

        Assert.Equal(2, set.ActiveShapes().Count);
    }

    [Fact]
    public void A_driver_scope_is_not_the_global_scope()
    {
        var set = new PodReportWarmSet();
        set.Record(new PodReportWarmKey(new DateTime(2026, 8, 1), new DateTime(2026, 8, 20), "global"));
        set.Record(new PodReportWarmKey(new DateTime(2026, 8, 1), new DateTime(2026, 8, 20), "driver-ABC"));

        Assert.Equal(2, set.ActiveShapes().Count);
    }

    /// <summary>
    /// A run of one-off custom ranges must not turn the warm job into a rebuild storm. The coldest
    /// shapes are dropped, so what survives is what somebody keeps coming back to.
    /// </summary>
    [Fact]
    public void The_tracked_set_is_bounded()
    {
        var set = new PodReportWarmSet();

        for (var days = 1; days <= 40; days++)
        {
            set.Record(Shape(days));
        }

        var active = set.ActiveShapes();
        Assert.True(active.Count <= 12, $"Expected the set to stay bounded; it held {active.Count}.");

        // The most recent request survives the trim.
        Assert.Contains(Shape(40), active.Select(shape => shape.Key));
    }

    [Fact]
    public void A_snapshot_well_inside_its_freshness_window_is_left_alone()
    {
        var snapshot = Snapshot(refreshedAt: Now.AddMinutes(-1), isFresh: true, creditNoteDataComplete: true);

        Assert.True(PodReportWarmJob.IsWarmEnough(snapshot, Freshness, Now));
    }

    [Fact]
    public void A_snapshot_about_to_lapse_is_rebuilt_before_anyone_hits_it()
    {
        // 12 minutes into a 15-minute window: 3 minutes left, inside the 5-minute refresh-ahead.
        var snapshot = Snapshot(refreshedAt: Now.AddMinutes(-12), isFresh: true, creditNoteDataComplete: true);

        Assert.False(PodReportWarmJob.IsWarmEnough(snapshot, Freshness, Now));
    }

    [Fact]
    public void A_missing_snapshot_is_rebuilt()
    {
        Assert.False(PodReportWarmJob.IsWarmEnough(null, Freshness, Now));
    }

    /// <summary>
    /// The reader rebuilds a snapshot with incomplete credit-note data however fresh it is, so
    /// warming has to agree — otherwise the job would skip exactly the snapshots that always miss.
    /// </summary>
    [Fact]
    public void A_fresh_snapshot_with_incomplete_credit_note_data_is_still_rebuilt()
    {
        var snapshot = Snapshot(refreshedAt: Now.AddMinutes(-1), isFresh: true, creditNoteDataComplete: false);

        Assert.False(PodReportWarmJob.IsWarmEnough(snapshot, Freshness, Now));
    }

    /// <summary>
    /// A scoped key is a hash of its shops. Rebuilding it without them rebuilt the global report
    /// instead: the scoped snapshot never warmed, and the global one was rebuilt against SAP on
    /// every fire for as long as the scoped shape stayed active.
    /// </summary>
    [Fact]
    public async Task A_scoped_shape_is_rebuilt_under_its_own_shops()
    {
        string[] shops = ["SPA059", "CIS006"];
        var scopeKey = GetPodUploadStatusHandler.BuildCacheScopeKey(false, shops)!;
        var set = new PodReportWarmSet();
        set.Record(new PodReportWarmKey(new DateTime(2026, 9, 1), new DateTime(2026, 9, 17), scopeKey), shops);

        var run = await RunJobAsync(set);

        Assert.Equal([scopeKey], run.CheckedScopeKeys);
        var query = Assert.Single(run.Sent);
        Assert.Null(query.UserId);
        Assert.True(query.IsWarmRebuild);
        Assert.NotNull(query.CustomerCodeScope);
        Assert.Equal(["CIS006", "SPA059"], query.CustomerCodeScope.Order(StringComparer.Ordinal));

        // The snapshot the handler saves is the one the job checked.
        Assert.Equal(scopeKey, GetPodUploadStatusHandler.BuildCacheScopeKey(false, query.CustomerCodeScope));
    }

    [Fact]
    public async Task A_global_shape_is_still_rebuilt_unscoped()
    {
        var set = new PodReportWarmSet();
        set.Record(Shape(30));

        var run = await RunJobAsync(set);

        Assert.Equal(["global"], run.CheckedScopeKeys);
        var query = Assert.Single(run.Sent);
        Assert.Null(query.UserId);
        Assert.True(query.IsWarmRebuild);
        Assert.Null(query.CustomerCodeScope);
        Assert.Equal(Shape(30).FromDate, query.FromDate);
        Assert.Equal(Shape(30).ToDate, query.ToDate);
    }

    /// <summary>
    /// The shops cannot be recovered from the hash, so a scoped shape without them is left for a
    /// person to rebuild. Falling back to an unscoped rebuild is exactly the defect.
    /// </summary>
    [Fact]
    public async Task A_scoped_shape_without_its_shops_is_skipped_not_rebuilt_globally()
    {
        var scopeKey = GetPodUploadStatusHandler.BuildCacheScopeKey(false, ["SPA059"])!;
        var set = new PodReportWarmSet();
        set.Record(new PodReportWarmKey(new DateTime(2026, 9, 1), new DateTime(2026, 9, 17), scopeKey));

        var run = await RunJobAsync(set);

        Assert.Empty(run.Sent);
    }

    [Fact]
    public async Task Shops_that_hash_to_a_different_scope_are_skipped()
    {
        var scopeKey = GetPodUploadStatusHandler.BuildCacheScopeKey(false, ["SPA059"])!;
        var set = new PodReportWarmSet();
        set.Record(
            new PodReportWarmKey(new DateTime(2026, 9, 1), new DateTime(2026, 9, 17), scopeKey),
            ["CIS006"]);

        var run = await RunJobAsync(set);

        Assert.Empty(run.Sent);
    }

    [Fact]
    public void The_warm_set_keeps_its_own_copy_of_the_shops()
    {
        var shops = new List<string> { "SPA059" };
        var scopeKey = GetPodUploadStatusHandler.BuildCacheScopeKey(false, shops)!;
        var set = new PodReportWarmSet();
        set.Record(new PodReportWarmKey(new DateTime(2026, 9, 1), new DateTime(2026, 9, 17), scopeKey), shops);

        shops.Add("CIS006");

        Assert.Equal(["SPA059"], Assert.Single(set.ActiveShapes()).CustomerCodes!);
    }

    private sealed record JobRun(List<string> CheckedScopeKeys, List<GetPodUploadStatusQuery> Sent);

    /// <summary>Runs the job against a cache where every snapshot is cold, capturing what it sends.</summary>
    private static async Task<JobRun> RunJobAsync(PodReportWarmSet set)
    {
        var run = new JobRun([], []);

        var cache = StubProxy.For<IPodReportCacheStore>((method, args) => method.Name switch
        {
            "get_Enabled" => (object)true,
            nameof(IPodReportCacheStore.GetAsync) => RecordCheck((string)args![2]!),
            _ => throw new InvalidOperationException($"IPodReportCacheStore.{method.Name} was not expected.")
        });

        var mediator = StubProxy.For<IMediator>((method, args) =>
        {
            if (method.Name != nameof(IMediator.Send))
                throw new InvalidOperationException($"Unexpected call to {method.Name}");

            run.Sent.Add((GetPodUploadStatusQuery)args![0]!);
            return Task.FromResult<ErrorOr<PodUploadStatusReportDto>>(new PodUploadStatusReportDto());
        });

        var services = new ServiceCollection();
        services.AddScoped(_ => cache);
        services.AddScoped(_ => mediator);

        var job = new PodReportWarmJob(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            set,
            Options.Create(new PodReportCacheSettings { Enabled = true, FreshnessMinutes = 15 }),
            NullLogger<PodReportWarmJob>.Instance);

        await job.Execute(StubProxy.For<IJobExecutionContext>((method, _) => method.Name switch
        {
            "get_CancellationToken" => CancellationToken.None,
            _ => throw new InvalidOperationException($"Unexpected Quartz call: {method.Name}")
        }));

        return run;

        Task<PodReportCacheSnapshot?> RecordCheck(string scopeKey)
        {
            run.CheckedScopeKeys.Add(scopeKey);
            return Task.FromResult<PodReportCacheSnapshot?>(null);
        }
    }

    /// <summary>
    /// The warm job rebuilds through the same handler a person's request goes through. If that
    /// rebuild counted as a request, a shape asked for once would be re-recorded on every rebuild and
    /// never leave the active window: rebuilt against SAP all day and all night, which is the
    /// fixed-timer warming the warm set exists to avoid.
    /// </summary>
    [Fact]
    public async Task Warming_stops_an_hour_after_the_last_real_request()
    {
        var clock = new ManualClock();
        var warmSet = new PodReportWarmSet(clock);
        var rig = CreateRig(warmSet);

        await rig.RequestAsync();

        var rebuildsAt = await RunJobEveryIntervalAsync(rig, clock, TimeSpan.FromHours(3));

        Assert.Empty(warmSet.ActiveShapes());
        Assert.NotEmpty(rebuildsAt);
        Assert.All(rebuildsAt, at => Assert.True(
            at <= PodReportWarmSet.ActiveWindow,
            $"The job rebuilt the report {at} after the only request, outside the {PodReportWarmSet.ActiveWindow} active window."));
    }

    /// <summary>
    /// Negative control for the test above: a shape somebody keeps coming back to stays warm, so that
    /// test cannot pass merely because the job never reaches the handler.
    /// </summary>
    [Fact]
    public async Task A_real_request_keeps_the_shape_warm()
    {
        var clock = new ManualClock();
        var warmSet = new PodReportWarmSet(clock);
        var rig = CreateRig(warmSet);

        await rig.RequestAsync();
        var firstRebuildsAt = await RunJobEveryIntervalAsync(rig, clock, TimeSpan.FromMinutes(50));

        await rig.RequestAsync();
        var secondRebuildsAt = await RunJobEveryIntervalAsync(rig, clock, TimeSpan.FromMinutes(50));

        // 100 minutes after the first request, but only 50 after the second.
        Assert.Equal([rig.Shape], warmSet.ActiveShapes().Select(shape => shape.Key));
        Assert.Equal(10, firstRebuildsAt.Count);
        Assert.Equal(10, secondRebuildsAt.Count);
    }

    /// <summary>
    /// The handler on its own: a warm rebuild leaves the shape's last-requested time where the last
    /// person put it, while a request from a person renews it.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Only_a_real_request_renews_the_shape(bool isWarmRebuild, bool activeAfterTheWindow)
    {
        var clock = new ManualClock();
        var warmSet = new PodReportWarmSet(clock);
        var rig = CreateRig(warmSet);

        await rig.RequestAsync();

        clock.Advance(TimeSpan.FromMinutes(50));
        await rig.RebuildAsync(new GetPodUploadStatusQuery(
            rig.Shape.FromDate,
            rig.Shape.ToDate,
            UserId: null,
            IsWarmRebuild: isWarmRebuild));

        // 70 minutes after the first request, 20 after the second call.
        clock.Advance(TimeSpan.FromMinutes(20));

        Assert.Equal(activeAfterTheWindow, warmSet.ActiveShapes().Any(shape => shape.Key == rig.Shape));
    }

    [Fact]
    public async Task A_warm_rebuild_does_not_start_tracking_a_shape_nobody_asked_for()
    {
        var warmSet = new PodReportWarmSet(new ManualClock());
        var rig = CreateRig(warmSet);

        await rig.RebuildAsync(new GetPodUploadStatusQuery(
            rig.Shape.FromDate,
            rig.Shape.ToDate,
            UserId: null,
            IsWarmRebuild: true));

        Assert.Empty(warmSet.ActiveShapes());
    }

    /// <summary>
    /// Runs the job on its production interval for <paramref name="duration"/>, returning how long
    /// into the run each rebuild happened.
    /// </summary>
    private static async Task<List<TimeSpan>> RunJobEveryIntervalAsync(
        WarmRig rig,
        ManualClock clock,
        TimeSpan duration)
    {
        var jobContext = StubProxy.For<IJobExecutionContext>((method, _) =>
            method.Name == "get_" + nameof(IJobExecutionContext.CancellationToken)
                ? CancellationToken.None
                : throw new InvalidOperationException($"IJobExecutionContext.{method.Name} was not expected."));
        var rebuildsAt = new List<TimeSpan>();

        for (var elapsed = JobInterval; elapsed <= duration; elapsed += JobInterval)
        {
            clock.Advance(JobInterval);

            var before = rig.Rebuilds;
            await rig.Job.Execute(jobContext);

            if (rig.Rebuilds > before)
            {
                rebuildsAt.Add(elapsed);
            }
        }

        return rebuildsAt;
    }

    /// <summary>
    /// The real job, handler and cache store (over SQLite), sharing one warm set. SAP is off and the
    /// cache stays empty, so the snapshot is always cold and the job reaches the handler on every run
    /// for as long as the shape is active.
    /// </summary>
    private WarmRig CreateRig(PodReportWarmSet warmSet)
    {
        var cacheSettings = new PodReportCacheSettings
        {
            Enabled = true,
            FreshnessMinutes = (int)Freshness.TotalMinutes,
            RetentionDays = 7
        };
        var store = new PodReportCacheStore(
            _context,
            Options.Create(cacheSettings),
            NullLogger<PodReportCacheStore>.Instance);
        var handler = new GetPodUploadStatusHandler(
            StubProxy.Unused<ISAPServiceLayerClient>(),
            StubProxy.Unused<IDocumentService>(),
            _context,
            Options.Create(new SAPSettings { Enabled = false }),
            Options.Create(new CreditNoteSyncSettings()),
            store,
            warmSet,
            NullLogger<GetPodUploadStatusHandler>.Instance);

        var rig = new WarmRig(handler);
        var mediator = StubProxy.For<IMediator>((method, args) =>
            method.Name == nameof(IMediator.Send) && args?[0] is GetPodUploadStatusQuery query
                ? rig.RebuildAsync(query)
                : throw new InvalidOperationException($"IMediator.{method.Name} was not expected."));

        var services = new ServiceCollection();
        services.AddSingleton<IPodReportCacheStore>(store);
        services.AddSingleton(mediator);

        rig.Job = new PodReportWarmJob(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            warmSet,
            Options.Create(cacheSettings),
            NullLogger<PodReportWarmJob>.Instance);

        return rig;
    }

    private sealed class WarmRig(GetPodUploadStatusHandler handler)
    {
        public PodReportWarmKey Shape { get; } =
            new(new DateTime(2026, 8, 1), new DateTime(2026, 8, 20), "global");

        public PodReportWarmJob Job { get; set; } = null!;

        public int Rebuilds { get; private set; }

        /// <summary>A person opening the report, as the controller sends it.</summary>
        public Task<ErrorOr<PodUploadStatusReportDto>> RequestAsync() =>
            handler.Handle(
                new GetPodUploadStatusQuery(Shape.FromDate, Shape.ToDate, UserId: null),
                CancellationToken.None);

        public Task<ErrorOr<PodUploadStatusReportDto>> RebuildAsync(GetPodUploadStatusQuery query)
        {
            Rebuilds++;
            return handler.Handle(query, CancellationToken.None);
        }
    }

    private static PodReportCacheSnapshot Snapshot(
        DateTime refreshedAt,
        bool isFresh,
        bool creditNoteDataComplete) =>
        new(new PodUploadStatusReportDto { CreditNoteDataComplete = creditNoteDataComplete },
            refreshedAt,
            isFresh);
}
