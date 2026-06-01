using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetTransferQueueStats;

public sealed class GetTransferQueueStatsHandler(
    ApplicationDbContext context
) : IRequestHandler<GetTransferQueueStatsQuery, ErrorOr<InventoryTransferQueueStatsDto>>
{
    public async Task<ErrorOr<InventoryTransferQueueStatsDto>> Handle(
        GetTransferQueueStatsQuery query,
        CancellationToken cancellationToken)
    {
        var stats = await context.InventoryTransferQueue
            .AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new InventoryTransferQueueStatsDto
            {
                TotalQueued = g.Count(),
                Pending = g.Count(q => q.Status == InventoryTransferQueueStatus.Pending),
                Processing = g.Count(q => q.Status == InventoryTransferQueueStatus.Processing),
                Completed = g.Count(q => q.Status == InventoryTransferQueueStatus.Completed),
                Failed = g.Count(q => q.Status == InventoryTransferQueueStatus.Failed),
                RequiresReview = g.Count(q => q.Status == InventoryTransferQueueStatus.RequiresReview),
                Cancelled = g.Count(q => q.Status == InventoryTransferQueueStatus.Cancelled)
            })
            .SingleOrDefaultAsync(cancellationToken)
            ?? new InventoryTransferQueueStatsDto();

        var oldestPending = await context.InventoryTransferQueue
            .AsNoTracking()
            .Where(q => q.Status == InventoryTransferQueueStatus.Pending)
            .OrderBy(q => q.CreatedAt)
            .Select(q => q.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (oldestPending != default)
        {
            stats.OldestPendingAge = oldestPending;
        }

        stats.TotalQuantityPending = await context.InventoryTransferQueue
            .AsNoTracking()
            .Where(q => q.Status == InventoryTransferQueueStatus.Pending)
            .SumAsync(q => q.TotalQuantity, cancellationToken);

        return stats;
    }
}
