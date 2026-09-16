using Quartz;
using ShopInventory.Services;

namespace ShopInventory.Configuration;

/// <summary>
/// Stops a node whose build is older than a live peer's from executing any clustered job.
/// </summary>
/// <remarks>
/// <para>
/// A veto happens after Quartz has handed this node the trigger, so the fire is recorded and the
/// trigger's next fire time is computed as normal; the job simply does not run here. The node that
/// holds the newer build takes the following fire, and every job in this build is an idempotent poller
/// or a scheduled sweep, so a fire skipped on one node is picked up on the next pass rather than lost.
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
    /// <summary>A repeat of the same refusal is logged at most this often, since triggers fire every few seconds.</summary>
    private static readonly TimeSpan LogInterval = TimeSpan.FromMinutes(5);

    private DateTime _lastLoggedUtc = DateTime.MinValue;

    public string Name => "stale-build-guard";

    public Task<bool> VetoJobExecution(
        ITrigger trigger,
        IJobExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (!registry.JobsAreVetoed)
        {
            return Task.FromResult(false);
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

        return Task.FromResult(true);
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
