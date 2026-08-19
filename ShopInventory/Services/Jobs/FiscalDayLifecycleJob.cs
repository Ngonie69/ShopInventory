using Quartz;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Services;

/// <summary>
/// Closes each device's fiscal day, packages it and uploads it, which is the step that actually puts a
/// van's receipts in front of ZIMRA.
///
/// Two triggers hang off this one job key. The evening one is the close itself, timed for after the vans
/// are in and their receipts drained. The hourly one exists for the two things a once-a-day pass cannot
/// do: warn while there is still day left to act on — the taxpayer's limit is a number of hours from the
/// handset's own opening time, which need not land anywhere near the evening — and pick up a day left half
/// way through by a run that died, rather than leaving it until tomorrow.
///
/// One key rather than two, for the reason spelled out beside the van and desktop posting triggers:
/// <see cref="DisallowConcurrentExecutionAttribute"/> is enforced per job key, and two keys could put an
/// hourly pass and the evening close over the same fiscal day at once — both generating the same file,
/// both uploading it.
/// </summary>
[DisallowConcurrentExecution]
public sealed class FiscalDayLifecycleJob(
    IServiceProvider serviceProvider,
    ILogger<FiscalDayLifecycleJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        using var scope = serviceProvider.CreateScope();
        var lifecycle = scope.ServiceProvider.GetRequiredService<FiscalDayLifecycleService>();

        try
        {
            var result = await lifecycle.AdvanceDueDaysAsync(context.CancellationToken);

            if (result.Skipped)
            {
                return;
            }

            if (result.ServiceAccountMissing)
            {
                // Once per pass, naming the one thing to do about it. The routes this needs are behind the
                // platform's bearer token, not the integration's API key, so an API key that works
                // everywhere else buys nothing here.
                logger.LogError(
                    "No fiscal day can be closed: no Fiscalisation service account is configured. Set "
                    + "Fiscalisation__FiscalDay__ServiceAccount__Username and __Password to a non-Admin platform "
                    + "account holding the fiscal-day and receipt-submit permissions. Nothing was attempted.");
                return;
            }

            if (result.DaysBlockedByOutstandingReceipts > 0)
            {
                logger.LogWarning(
                    "{Blocked} fiscal day(s) were left open because {Receipts} signed receipt(s) have not reached "
                    + "the platform. Closing over them would strand those receipts permanently, so the days wait.",
                    result.DaysBlockedByOutstandingReceipts,
                    result.OutstandingReceipts);
            }

            if (result.NeedsReconciliation > 0 || result.Failed > 0)
            {
                logger.LogError(
                    "{Unknown} fiscal day(s) have an unknown outcome and {Failed} were refused outright. They are "
                    + "in the exception center and must be resolved by reading FDMS, never by closing or uploading "
                    + "again. {Errors}",
                    result.NeedsReconciliation,
                    result.Failed,
                    string.Join(" | ", result.Errors.Take(10)));
            }
            else if (result.Errors.Count > 0)
            {
                logger.LogWarning(
                    "Fiscal day lifecycle left {Count} day(s) where they were; the next pass picks them up. {Errors}",
                    result.Errors.Count,
                    string.Join(" | ", result.Errors.Take(10)));
            }

            if (result.DaysSubmitted > 0 || result.DaysClosed > 0)
            {
                logger.LogInformation(
                    "Fiscal day lifecycle: {Tracked} day(s) tracked, {Closed} closed at FDMS, {Submitted} now with "
                    + "ZIMRA, {Reconciled} settled by reading rather than repeating.",
                    result.DaysTracked,
                    result.DaysClosed,
                    result.DaysSubmitted,
                    result.Reconciled);
            }
        }
        catch (Exception ex)
        {
            // The next trigger is the right retry. Every day's progress is persisted step by step, so
            // nothing done so far is repeated and nothing outstanding is lost.
            logger.LogError(ex, "The fiscal day lifecycle pass failed.");
        }
    }
}
