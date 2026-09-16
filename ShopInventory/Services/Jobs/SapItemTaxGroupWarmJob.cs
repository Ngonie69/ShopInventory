using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using ShopInventory.Features.Sync.Commands.SyncItemTaxGroups;

namespace ShopInventory.Services;

/// <summary>
/// Keeps a local copy of the VAT group each item is sold under, so a sale can be taxed correctly
/// without a SAP read in front of a waiting customer.
/// </summary>
/// <remarks>
/// The item master is the only place that answers what an item's tax is. Reading it costs a paged
/// sweep of every valid item against a SAP concurrency limit shared with everything else the process
/// does, which is not a price a till sale can pay — the same reasoning
/// <see cref="SapItemUomWarmJob"/> is written down for.
///
/// <para>
/// Runs nightly because that is how often the answer usually changes: an item's VAT group moves when
/// the tax law does, or when somebody corrects a master-data mistake. A correction that cannot wait
/// for the night is run from Settings → Data Sync, which sends the same
/// <see cref="SyncItemTaxGroupsCommand"/>.
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class SapItemTaxGroupWarmJob(
    IServiceScopeFactory scopeFactory,
    ILogger<SapItemTaxGroupWarmJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        using var scope = scopeFactory.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new SyncItemTaxGroupsCommand(), context.CancellationToken);

        // Not thrown. The handler has already logged the cause and left the stored copy as it was,
        // and a stale copy is still the right thing to tax from until tomorrow's pass.
        if (result.IsError)
        {
            logger.LogWarning(
                "Nightly item VAT group sync did not complete: {Errors}",
                string.Join("; ", result.Errors.Select(error => error.Description)));
        }
    }
}
