using Microsoft.Extensions.Options;
using Quartz;
using Quartz.Impl.Matchers;

namespace ShopInventory.Configuration;

/// <summary>
/// Deletes from the persistent job store any job this build no longer declares.
/// </summary>
/// <remarks>
/// Quartz's container configuration only ever adds or replaces stored jobs; it never removes one.
/// Every job here is StoreDurably() in a clustered Postgres store, so a job the code stops
/// registering — a flag turned off, a job deleted — keeps its stored trigger and keeps firing on
/// every node until someone deletes the rows by hand. That is how the morning low-stock sweep went
/// on running at 07:30 for days after it was turned off: the build declared twelve jobs, the store
/// still held a thirteenth. This runs once at startup, after the declared set has been written, and
/// removes the rest, so what the code registers is what the store runs.
///
/// The declared set is read from <see cref="QuartzOptions.JobDetails"/>, which is where
/// <c>AddJob</c> records every job <see cref="QuartzConfiguration"/> registers, so there is no
/// second list to keep in step. A job removed here was declared by no build sharing the store, or by
/// an older one on its way out during a blue/green overlap; an old node that restarts declares it
/// again, and a new one removes it again. Only jobs registered through the container exist today;
/// anything scheduled at runtime in future would need its own group excluded from the sweep.
/// </remarks>
internal sealed class QuartzStoredJobReconciler(
    ISchedulerFactory schedulerFactory,
    IOptions<QuartzOptions> quartzOptions,
    ILogger<QuartzStoredJobReconciler> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var declared = quartzOptions.Value.JobDetails.Select(job => job.Key).ToHashSet();

            // The first GetScheduler call is what writes the declared jobs to the store, so once it
            // returns the store holds this build's set plus whatever earlier builds left behind.
            var scheduler = await schedulerFactory.GetScheduler(cancellationToken);
            var stored = await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), cancellationToken);
            var stale = stored.Where(key => !declared.Contains(key)).OrderBy(key => key.ToString()).ToList();

            if (stale.Count == 0)
            {
                logger.LogDebug("Quartz job store matches the {Count} declared job(s); nothing to remove", declared.Count);
                return;
            }

            foreach (var key in stale)
            {
                var deleted = await scheduler.DeleteJob(key, cancellationToken);
                logger.LogInformation(
                    "Removed stored Quartz job {JobKey} and its triggers: this build does not declare it (deleted={Deleted})",
                    key,
                    deleted);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Not worth failing startup over: the declared jobs are scheduled either way, and a stale
            // one merely keeps firing until the next start gets to remove it.
            logger.LogWarning(
                ex,
                "Could not reconcile the Quartz job store against the declared jobs; a job this build no longer declares may keep firing until the next start");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
