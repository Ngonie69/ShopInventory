using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Services;

namespace ShopInventory.Features.Sync.Commands.WarmItemUoms;

public sealed class WarmItemUomsHandler(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    BackgroundWorkerLeaderElector leaderElector,
    ILogger<WarmItemUomsHandler> logger
) : IRequestHandler<WarmItemUomsCommand, ErrorOr<ItemUomWarmResult>>
{
    private const string ClusterLockName = "item-uom-warm-run";

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

    private static readonly SemaphoreSlim WarmLock = new(1, 1);

    public async Task<ErrorOr<ItemUomWarmResult>> Handle(WarmItemUomsCommand command, CancellationToken cancellationToken)
    {
        // Two passes would resolve the same pairs twice against the same shared SAP slots. The
        // in-process gate covers two presses on one node; the advisory lock covers two nodes.
        if (!await WarmLock.WaitAsync(0, cancellationToken))
        {
            return Errors.Sync.ItemUomWarmAlreadyRunning;
        }

        try
        {
            await using var clusterLease = await leaderElector.TryAcquireAsync(ClusterLockName, cancellationToken);
            if (clusterLease is null)
            {
                return Errors.Sync.ItemUomWarmAlreadyRunning;
            }

            var since = DateTime.UtcNow - OrderHistoryWindow;

            var orderedPairs = await (
                    from line in context.SalesOrderLines.AsNoTracking()
                    join order in context.SalesOrders.AsNoTracking() on line.SalesOrderId equals order.Id
                    where order.CreatedAt >= since
                    select new { line.ItemCode, line.UoMCode })
                .Distinct()
                .ToListAsync(cancellationToken);

            var catalogPairs = await context.Products
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

            var pairs = passes.Sum(pass => pass.Count());
            var warmed = 0;
            var failedBatches = 0;
            string? lastError = null;

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
                        // Best-effort per batch: an approval can still resolve these on demand, and
                        // one bad batch should not cost the rest of the pass.
                        failedBatches++;
                        lastError = ex.Message;
                        logger.LogWarning(
                            ex,
                            "Failed to warm SAP UoM mappings for a batch of {PairCount} item/UoM pair(s)",
                            batch.Length);
                    }
                }
            }

            logger.LogInformation(
                "SAP item UoM warm pass covered {Warmed} of {Pairs} item/UoM pair(s); {FailedBatches} batch(es) failed",
                warmed, pairs, failedBatches);

            // Nothing answered at all is a failed sync, so Data Sync says so; a partial pass is not.
            if (pairs > 0 && warmed == 0)
            {
                return Errors.Sync.ItemUomWarmFailed(pairs, lastError ?? string.Empty);
            }

            return new ItemUomWarmResult(pairs, warmed, failedBatches, DateTime.UtcNow);
        }
        finally
        {
            WarmLock.Release();
        }
    }
}
