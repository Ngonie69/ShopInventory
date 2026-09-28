using Quartz;
using ShopInventory.Services;

namespace ShopInventory.Configuration;

/// <summary>
/// Stops a node whose build is older than a live peer's from executing any clustered job.
/// </summary>
/// <remarks>
/// <para>
/// A veto happens after Quartz has handed this node the trigger, so the fire is recorded and the
/// trigger's next fire time is computed as normal; the job simply does not run here. For an interval
/// job that costs nothing: the next pass, seconds or minutes away, goes to whichever node takes it.
/// </para>
/// <para>
/// A daily or one-off fire has no next pass to fall back on. Refused and left alone, that day's
/// 07:00 snapshot or 16:45 consolidation just did not happen. So a refused fire of a cron or
/// one-shot trigger is scheduled again, <see cref="RefireDelay"/> later, as a one-shot copy the whole
/// cluster can take. The copy keeps the trigger's name, which several jobs read to tell their passes
/// apart, and its data, and sits in a group of its own (<see cref="RefireGroupPrefix"/>) so it can
/// never collide with the original. Should this node take the copy too, it is refused and copied
/// again, until a node on the newer build runs it.
/// </para>
/// <para>
/// Putting this node's scheduler in standby instead would stop it taking triggers at all, but the
/// "workers" health check reads standby as a stopped scheduler, and that check is part of
/// /health/ready, so a node still serving HTTP perfectly well would be taken for not ready.
/// </para>
/// <para>
/// One listener covers every job by construction. The alternative — remembering to guard each job —
/// is the thing that failed: the daily payment was correct in the build that mattered, and the node
/// nobody deployed to kept running the job it had.
/// </para>
/// </remarks>
public sealed class StaleBuildJobGuard(
    ClusterBuildRegistry registry,
    ILogger<StaleBuildJobGuard> logger) : ITriggerListener
{
    /// <summary>The trigger group prefix of a refused fire's copy; the rest of the group name is unique.</summary>
    public const string RefireGroupPrefix = "stale-build-refire:";

    /// <summary>How long after a refusal its copy fires, so a newer node has a turn at taking it.</summary>
    public static readonly TimeSpan RefireDelay = TimeSpan.FromSeconds(15);

    /// <summary>A repeat of the same refusal is logged at most this often, since triggers fire every few seconds.</summary>
    private static readonly TimeSpan LogInterval = TimeSpan.FromMinutes(5);

    private DateTime _lastLoggedUtc = DateTime.MinValue;

    public string Name => "stale-build-guard";

    public async Task<bool> VetoJobExecution(
        ITrigger trigger,
        IJobExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (!registry.JobsAreVetoed)
        {
            return false;
        }

        var utcNow = DateTime.UtcNow;
        if (utcNow - _lastLoggedUtc >= LogInterval)
        {
            _lastLoggedUtc = utcNow;
            logger.LogWarning(
                "Not running {Job} on this node: it holds an older build than another node in the cluster. {Reason}",
                context.JobDetail.Key.Name, registry.VetoReason);
        }
        else
        {
            logger.LogDebug("Not running {Job} on this node: older build.", context.JobDetail.Key.Name);
        }

        if (HasNoNextPass(trigger))
        {
            await RefireElsewhereAsync(trigger, context, cancellationToken);
        }

        return true;
    }

    /// <summary>A cron trigger, or one that fires once: a refused fire of either is not repeated by itself.</summary>
    public static bool HasNoNextPass(ITrigger trigger) =>
        trigger is ICronTrigger || trigger is ISimpleTrigger { RepeatCount: 0 };

    private async Task RefireElsewhereAsync(
        ITrigger trigger,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        var copy = TriggerBuilder.Create()
            .WithIdentity(trigger.Key.Name, RefireGroupPrefix + Guid.NewGuid().ToString("N"))
            .ForJob(trigger.JobKey)
            .UsingJobData(new JobDataMap((IDictionary<string, object>)trigger.JobDataMap))
            .WithPriority(trigger.Priority)
            .WithDescription($"{trigger.Key} again: a node on an older build refused it.")
            .StartAt(DateTimeOffset.UtcNow.Add(RefireDelay))
            .WithSimpleSchedule(schedule => schedule.WithMisfireHandlingInstructionFireNow())
            .Build();

        try
        {
            await context.Scheduler.ScheduleJob(copy, cancellationToken);

            if (trigger.Key.Group.StartsWith(RefireGroupPrefix, StringComparison.Ordinal))
            {
                logger.LogDebug("Refused {Trigger} again; copied it once more for a newer node.", trigger.Key);
            }
            else
            {
                logger.LogInformation(
                    "Refused {Trigger} on this older build; it is scheduled again for {At:HH:mm:ss} UTC so a node on the newer build runs it.",
                    trigger.Key, copy.StartTimeUtc);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The refusal stands either way; this node must not run the job.
            logger.LogError(ex, "Refused {Trigger} on this older build and could not schedule it again. That fire is lost.", trigger.Key);
        }
    }

    public Task TriggerFired(ITrigger trigger, IJobExecutionContext context, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task TriggerMisfired(ITrigger trigger, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task TriggerComplete(
        ITrigger trigger,
        IJobExecutionContext context,
        SchedulerInstruction triggerInstructionCode,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
