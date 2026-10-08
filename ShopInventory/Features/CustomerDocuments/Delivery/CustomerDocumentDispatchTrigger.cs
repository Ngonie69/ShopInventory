using Microsoft.Extensions.Options;
using Quartz;
using ShopInventory.Configuration;
using ShopInventory.Services;

namespace ShopInventory.Features.CustomerDocuments.Delivery;

/// <summary>
/// Fires the clustered delivery job once, now.
/// </summary>
/// <remarks>
/// Through the scheduler rather than by sending from the request, so a send asked for on a node with
/// no gateway configured still goes — from whichever node runs the job — and so every send keeps to
/// the one pacing and duplicate guard. The job allows no concurrent run, so a trigger while a pass is
/// running simply runs next.
/// </remarks>
public sealed class CustomerDocumentDispatchTrigger(
    ISchedulerFactory schedulerFactory,
    IOptions<CustomerDocumentDeliverySettings> options,
    ILogger<CustomerDocumentDispatchTrigger> logger) : ICustomerDocumentDispatchTrigger
{
    public async Task TriggerAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        try
        {
            var scheduler = await schedulerFactory.GetScheduler(cancellationToken);
            await scheduler.TriggerJob(new JobKey(CustomerDocumentDeliveryJob.JobName), cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not wake the customer document delivery job; the next scheduled pass will send.");
        }
    }
}
