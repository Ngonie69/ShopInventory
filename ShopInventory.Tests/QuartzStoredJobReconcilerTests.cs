using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Quartz;
using Quartz.Impl.Matchers;
using ShopInventory.Configuration;

namespace ShopInventory.Tests;

/// <summary>
/// Which stored Quartz jobs the startup sweep deletes, and — the half that broke a deployment — which
/// it must leave alone.
/// </summary>
/// <remarks>
/// The sweep exists because the job store is durable and clustered: a job the build stops declaring
/// keeps its trigger rows and keeps firing on every node. It deletes by "the running build does not
/// declare this", which is fine until two builds share the store, as they do for the length of every
/// blue/green swap.
///
/// <para>
/// Quartz introduces a build's jobs in one pass and their triggers in the next, so a job being added by
/// the incoming node is briefly in the store with no triggers — and to the outgoing node, which never
/// heard of it, it looks exactly like a retired job. Deleting it there made the incoming node's next
/// statement fail with "the job referenced by the trigger does not exist" and its host fail to start:
/// the deploy could not go live at all. The triggerless exemption below is what stops that.
/// </para>
/// </remarks>
public sealed class QuartzStoredJobReconcilerTests
{
    [Fact]
    public async Task Deletes_a_stored_job_this_build_no_longer_declares()
    {
        var scheduler = await BuildSchedulerAsync();
        await StoreJobWithTriggerAsync(scheduler, "retired-sweep");

        await RunReconcilerAsync(scheduler, declared: []);

        var remaining = await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup());
        Assert.DoesNotContain(new JobKey("retired-sweep"), remaining);
    }

    [Fact]
    public async Task Keeps_a_stored_job_this_build_still_declares()
    {
        var scheduler = await BuildSchedulerAsync();
        await StoreJobWithTriggerAsync(scheduler, "invoice-posting");

        await RunReconcilerAsync(scheduler, declared: ["invoice-posting"]);

        var remaining = await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup());
        Assert.Contains(new JobKey("invoice-posting"), remaining);
    }

    /// <summary>
    /// The regression: an undeclared job with no triggers is another node mid-introduction, and deleting
    /// it takes that node's startup down with it.
    /// </summary>
    [Fact]
    public async Task Keeps_an_undeclared_job_that_has_no_triggers_yet()
    {
        var scheduler = await BuildSchedulerAsync();

        // Exactly what a peer node has written by the time it has stored its jobs and is about to store
        // their triggers — the state the outgoing build read as "stale" and deleted.
        await scheduler.AddJob(
            JobBuilder.Create<NoOpJob>().WithIdentity("pod-report-warm").StoreDurably().Build(),
            replace: true);

        await RunReconcilerAsync(scheduler, declared: []);

        // The peer's very next statement. Without the exemption this throws the production error
        // verbatim — "The job (DEFAULT.pod-report-warm) referenced by the trigger does not exist" —
        // which is the throw that failed the host and with it the deployment.
        await scheduler.ScheduleJob(TriggerFor("pod-report-warm"));

        Assert.Single(await scheduler.GetTriggersOfJob(new JobKey("pod-report-warm")));
        Assert.Contains(new JobKey("pod-report-warm"), await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup()));
    }

    /// <summary>
    /// The exemption is per job, so a genuinely retired job is still swept in the same pass that spares
    /// the one being introduced.
    /// </summary>
    [Fact]
    public async Task Sweeps_a_retired_job_while_sparing_a_triggerless_one()
    {
        var scheduler = await BuildSchedulerAsync();
        await StoreJobWithTriggerAsync(scheduler, "low-stock-sweep");
        await scheduler.AddJob(
            JobBuilder.Create<NoOpJob>().WithIdentity("pod-report-warm").StoreDurably().Build(),
            replace: true);

        await RunReconcilerAsync(scheduler, declared: []);

        var remaining = await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup());
        Assert.DoesNotContain(new JobKey("low-stock-sweep"), remaining);
        Assert.Contains(new JobKey("pod-report-warm"), remaining);
    }

    private static async Task RunReconcilerAsync(IScheduler scheduler, string[] declared)
    {
        var options = new QuartzOptions();
        foreach (var name in declared)
        {
            options.AddJob<NoOpJob>(job => job.WithIdentity(name).StoreDurably());
        }

        var reconciler = new QuartzStoredJobReconciler(
            new FixedSchedulerFactory(scheduler),
            Options.Create(options),
            NullLogger<QuartzStoredJobReconciler>.Instance);

        await reconciler.StartAsync(CancellationToken.None);
    }

    private static async Task<IScheduler> BuildSchedulerAsync()
    {
        // A RAM store, not Postgres: what is under test is which keys the sweep chooses, and that choice
        // is made from GetJobKeys and GetTriggersOfJob, which every store answers the same way.
        var builder = SchedulerBuilder.Create()
            .WithId(Guid.NewGuid().ToString("N"))
            .WithName(Guid.NewGuid().ToString("N"))
            .UseDefaultThreadPool(pool => pool.MaxConcurrency = 1);

        return await builder.BuildScheduler();
    }

    private static async Task StoreJobWithTriggerAsync(IScheduler scheduler, string name)
    {
        await scheduler.ScheduleJob(
            JobBuilder.Create<NoOpJob>().WithIdentity(name).StoreDurably().Build(),
            TriggerFor(name));
    }

    private static ITrigger TriggerFor(string name) =>
        TriggerBuilder.Create()
            .WithIdentity($"{name}-trigger")
            .ForJob(new JobKey(name))
            .StartAt(DateTimeOffset.UtcNow.AddHours(1))
            .Build();

    private sealed class NoOpJob : IJob
    {
        public Task Execute(IJobExecutionContext context) => Task.CompletedTask;
    }

    private sealed class FixedSchedulerFactory(IScheduler scheduler) : ISchedulerFactory
    {
        public Task<IReadOnlyList<IScheduler>> GetAllSchedulers(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<IScheduler>>([scheduler]);

        public Task<IScheduler> GetScheduler(CancellationToken cancellationToken = default) =>
            Task.FromResult(scheduler);

        public Task<IScheduler?> GetScheduler(string schedName, CancellationToken cancellationToken = default) =>
            Task.FromResult<IScheduler?>(scheduler);
    }
}
