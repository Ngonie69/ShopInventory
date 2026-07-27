using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using ShopInventory.Data;

namespace ShopInventory.Services;

/// <summary>
/// Pre-resolves the canonical SAP unit of measure for the item / UoM pairs orders actually use,
/// so approvals find them already stored.
/// </summary>
/// <remarks>
/// Resolving a UoM costs several SQLQueries round-trips per batch of items against a SAP
/// concurrency limit shared with everything else the process does. Paying that on the approval
/// path is what made approvals take minutes. The resolutions are durable, so this only ever has
/// real work to do for items nobody has ordered before — or for items whose UoM could not be
/// resolved last time, which are retried here rather than in front of a waiting rep.
/// </remarks>
[DisallowConcurrentExecution]
public sealed class SapItemUomWarmJob : IJob
{
    /// <summary>
    /// Pairs per resolver call. The resolver batches SAP queries in forties internally; this only
    /// bounds how much is in flight at once and how much a single failure costs.
    /// </summary>
    private const int WarmBatchSize = 200;

    /// <summary>
    /// How far back to read order history for pairs. Long enough to cover seasonal lines, short
    /// enough that delisted items are not retried forever.
    /// </summary>
    private static readonly TimeSpan OrderHistoryWindow = TimeSpan.FromDays(120);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SapItemUomWarmJob> _logger;

    public SapItemUomWarmJob(
        IServiceScopeFactory scopeFactory,
        ILogger<SapItemUomWarmJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var cancellationToken = context.CancellationToken;

        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var sapClient = scope.ServiceProvider.GetRequiredService<ISAPServiceLayerClient>();

        var since = DateTime.UtcNow - OrderHistoryWindow;

        var orderedPairs = await (
                from line in dbContext.SalesOrderLines.AsNoTracking()
                join order in dbContext.SalesOrders.AsNoTracking() on line.SalesOrderId equals order.Id
                where order.CreatedAt >= since
                select new { line.ItemCode, line.UoMCode })
            .Distinct()
            .ToListAsync(cancellationToken);

        var catalogPairs = await dbContext.Products
            .AsNoTracking()
            .Where(product => product.IsActive)
            .Select(product => new { product.ItemCode, UoMCode = product.SalesUnit })
            .Distinct()
            .ToListAsync(cancellationToken);

        // An item code may appear at most once per resolver call — the resolver looks the
        // requested UoM up by item — so pairs are grouped into passes that each hold it once.
        var passes = orderedPairs
            .Concat(catalogPairs)
            .Where(pair => !string.IsNullOrWhiteSpace(pair.ItemCode))
            .Select(pair => (ItemCode: pair.ItemCode!.Trim(), RequestedUomCode: pair.UoMCode?.Trim()))
            .Distinct()
            .GroupBy(pair => pair.ItemCode, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group.Select((pair, index) => (Pass: index, pair.ItemCode, pair.RequestedUomCode)))
            .GroupBy(entry => entry.Pass)
            .OrderBy(group => group.Key)
            .ToList();

        var warmed = 0;
        foreach (var pass in passes)
        {
            foreach (var batch in pass.Chunk(WarmBatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await sapClient.WarmSalesOrderLineSapUomsAsync(
                        batch.Select(entry => ((string?)entry.ItemCode, (string?)entry.RequestedUomCode)),
                        cancellationToken);
                    warmed += batch.Length;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Warming is best-effort: an approval can still resolve on demand. Failing the
                    // job would only retrigger the same SAP calls on the next fire.
                    _logger.LogWarning(
                        ex,
                        "Failed to warm SAP UoM mappings for a batch of {PairCount} item/UoM pair(s)",
                        batch.Length);
                }
            }
        }

        _logger.LogInformation("SAP item UoM warm pass covered {PairCount} item/UoM pair(s)", warmed);
    }
}
