using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;
using Quartz.Impl;
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

        public Task Execute(IJobExecutionContext context)
        {
            Ran.TrySetResult(true);
            return Task.CompletedTask;
        }
    }
}
