using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;
using Quartz.Impl;
using Quartz.Impl.Matchers;
using ShopInventory.Common.Cluster;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The guard against a real scheduler: a veto has to actually stop the job, not merely be returned.
/// </summary>
public sealed class StaleBuildJobGuardTests : IAsyncLifetime
{
    private static readonly DateTime OldBuild = new(2026, 9, 12, 5, 32, 0, DateTimeKind.Utc);
    private static readonly DateTime NewBuild = new(2026, 9, 15, 13, 33, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private ServiceProvider _provider = null!;
    private IScheduler? _scheduler;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        if (_scheduler is not null)
        {
            await _scheduler.Shutdown(waitForJobsToComplete: false);
        }

        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task A_node_on_an_older_build_runs_nothing()
    {
        await AddPeerAsync(NewBuild);

        var ran = await RunOneJobAsync(myBuild: OldBuild);

        Assert.False(ran);
    }

    [Fact]
    public async Task The_same_job_runs_when_no_peer_is_newer()
    {
        await AddPeerAsync(OldBuild);

        var ran = await RunOneJobAsync(myBuild: NewBuild);

        Assert.True(ran);
    }

    [Fact]
    public async Task A_refused_one_off_fire_is_scheduled_again_for_another_node()
    {
        await AddPeerAsync(NewBuild);
        var scheduler = (await StartGuardedSchedulerAsync(myBuild: OldBuild)).Scheduler;

        // Fired by hand: Quartz does that through a one-off trigger of its own, the same shape as the
        // startup triggers, and a refused one-off has no next fire either.
        await scheduler.ScheduleJob(
            JobBuilder.Create<RecordingJob>().WithIdentity("daily-job").StoreDurably().Build(),
            TriggerBuilder.Create()
                .WithIdentity("daily-job-trigger")
                .UsingJobData("pass", "nightly")
                .WithCronSchedule("0 0 7 * * ?")
                .Build());
        var before = DateTimeOffset.UtcNow;
        await scheduler.TriggerJob(new JobKey("daily-job"));
        await Task.Delay(TimeSpan.FromSeconds(2));

        Assert.False(RecordingJob.Ran.Task.IsCompleted);
        var copy = Assert.Single(await RefireCopiesAsync(scheduler));
        Assert.Equal(new JobKey("daily-job"), copy.JobKey);
        Assert.InRange(
            copy.StartTimeUtc,
            before.Add(StaleBuildJobGuard.RefireDelay).AddSeconds(-1),
            before.Add(StaleBuildJobGuard.RefireDelay).AddSeconds(5));
    }

    [Fact]
    public async Task A_refused_cron_fire_keeps_its_trigger_name_and_data()
    {
        await AddPeerAsync(NewBuild);
        var scheduler = (await StartGuardedSchedulerAsync(myBuild: OldBuild)).Scheduler;

        // Fires every second on its own schedule, the way a real cron fire reaches the guard.
        await scheduler.ScheduleJob(
            JobBuilder.Create<RecordingJob>().WithIdentity("rollup").Build(),
            TriggerBuilder.Create()
                .WithIdentity("cartrack-day-rollup-hourly-trigger")
                .UsingJobData("pass", "hourly")
                .WithCronSchedule("0/1 * * * * ?")
                .Build());
        await Task.Delay(TimeSpan.FromSeconds(3));
        await scheduler.PauseTrigger(new TriggerKey("cartrack-day-rollup-hourly-trigger"));

        var copies = await RefireCopiesAsync(scheduler);
        Assert.NotEmpty(copies);
        Assert.All(copies, copy =>
        {
            Assert.Equal("cartrack-day-rollup-hourly-trigger", copy.Key.Name);
            Assert.Equal("hourly", copy.JobDataMap.GetString("pass"));
        });
    }

    [Fact]
    public async Task A_refused_interval_fire_is_left_to_its_next_pass()
    {
        await AddPeerAsync(NewBuild);
        var scheduler = (await StartGuardedSchedulerAsync(myBuild: OldBuild)).Scheduler;

        await scheduler.ScheduleJob(
            JobBuilder.Create<RecordingJob>().WithIdentity("poller").Build(),
            TriggerBuilder.Create()
                .WithIdentity("poller-trigger")
                .StartNow()
                .WithSimpleSchedule(schedule => schedule.WithInterval(TimeSpan.FromSeconds(1)).RepeatForever())
                .Build());
        await Task.Delay(TimeSpan.FromSeconds(3));

        Assert.False(RecordingJob.Ran.Task.IsCompleted);
        Assert.Empty(await RefireCopiesAsync(scheduler));
    }

    [Fact]
    public async Task The_copy_runs_once_no_newer_node_is_live_and_the_job_sees_the_original_name()
    {
        await AddPeerAsync(NewBuild);
        var (registry, scheduler) = await StartGuardedSchedulerAsync(myBuild: OldBuild);

        // A cron trigger refused on its own schedule, then held so it fires no more by itself.
        await scheduler.ScheduleJob(
            JobBuilder.Create<RecordingJob>().WithIdentity("daily-job").Build(),
            TriggerBuilder.Create().WithIdentity("daily-job-trigger").WithCronSchedule("0/1 * * * * ?").Build());
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        await scheduler.PauseTrigger(new TriggerKey("daily-job-trigger"));
        Assert.False(RecordingJob.Ran.Task.IsCompleted);
        Assert.NotEmpty(await RefireCopiesAsync(scheduler));

        // Stands in for a node on the newer build taking the copy: the newer peer goes away, so this
        // node is no longer held back when the copy comes due.
        using (var scope = _provider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await context.ClusterNodes.Where(node => node.MachineName == "KFL-DNS2").ExecuteDeleteAsync();
        }

        await registry.RefreshAsync(CancellationToken.None);
        Assert.False(registry.JobsAreVetoed);

        var finished = await Task.WhenAny(
            RecordingJob.Ran.Task,
            Task.Delay(StaleBuildJobGuard.RefireDelay + TimeSpan.FromSeconds(10)));
        Assert.Same(RecordingJob.Ran.Task, finished);
        Assert.Equal("daily-job-trigger", RecordingJob.TriggerName);
        Assert.StartsWith(StaleBuildJobGuard.RefireGroupPrefix, RecordingJob.TriggerGroup);
    }

    private async Task<(ClusterBuildRegistry Registry, IScheduler Scheduler)> StartGuardedSchedulerAsync(DateTime myBuild)
    {
        var registry = new ClusterBuildRegistry(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            new BuildStampProvider(myBuild),
            NullLogger<ClusterBuildRegistry>.Instance);
        await registry.RefreshAsync(CancellationToken.None);

        _scheduler = await new StdSchedulerFactory(new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = "guard-test-" + Guid.NewGuid().ToString("N")
        }).GetScheduler();
        _scheduler.ListenerManager.AddTriggerListener(
            new StaleBuildJobGuard(registry, NullLogger<StaleBuildJobGuard>.Instance));
        RecordingJob.Ran = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingJob.TriggerName = null;
        RecordingJob.TriggerGroup = null;
        await _scheduler.Start();
        return (registry, _scheduler);
    }

    private static async Task<List<ITrigger>> RefireCopiesAsync(IScheduler scheduler)
    {
        var keys = await scheduler.GetTriggerKeys(GroupMatcher<TriggerKey>.GroupStartsWith(StaleBuildJobGuard.RefireGroupPrefix));
        var copies = new List<ITrigger>();
        foreach (var key in keys)
        {
            copies.Add((await scheduler.GetTrigger(key))!);
        }

        return copies;
    }

    private async Task AddPeerAsync(DateTime build)
    {
        using var scope = _provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        context.ClusterNodes.Add(new ClusterNodeEntity
        {
            NodeKey = "KFL-DNS2|C:/inetpub/api-green",
            MachineName = "KFL-DNS2",
            ContentRoot = "C:/inetpub/api-green",
            BuildTimestampUtc = build,
            StartedAtUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }

    /// <summary>Schedules one job on a real scheduler with the guard attached; true when it ran.</summary>
    private async Task<bool> RunOneJobAsync(DateTime myBuild)
    {
        var registry = new ClusterBuildRegistry(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            new BuildStampProvider(myBuild),
            NullLogger<ClusterBuildRegistry>.Instance);

        await registry.RefreshAsync(CancellationToken.None);

        _scheduler = await new StdSchedulerFactory().GetScheduler();
        _scheduler.ListenerManager.AddTriggerListener(
            new StaleBuildJobGuard(registry, NullLogger<StaleBuildJobGuard>.Instance));

        RecordingJob.Ran = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var job = JobBuilder.Create<RecordingJob>().WithIdentity("recording-job").Build();
        var trigger = TriggerBuilder.Create().WithIdentity("recording-trigger").StartNow().Build();

        await _scheduler.ScheduleJob(job, trigger);
        await _scheduler.Start();

        // Long enough for a fire that is going to happen to have happened.
        var finished = await Task.WhenAny(RecordingJob.Ran.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        return finished == RecordingJob.Ran.Task;
    }

    private sealed class RecordingJob : IJob
    {
        internal static TaskCompletionSource<bool> Ran = new();

        internal static string? TriggerName;
        internal static string? TriggerGroup;

        public Task Execute(IJobExecutionContext context)
        {
            TriggerName = context.Trigger.Key.Name;
            TriggerGroup = context.Trigger.Key.Group;
            Ran.TrySetResult(true);
            return Task.CompletedTask;
        }
    }
}
