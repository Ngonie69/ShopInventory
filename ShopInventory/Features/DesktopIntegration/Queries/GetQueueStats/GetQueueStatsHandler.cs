using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetQueueStats;

public sealed class GetQueueStatsHandler(
    ApplicationDbContext context
) : IRequestHandler<GetQueueStatsQuery, ErrorOr<InvoiceQueueStatsDto>>
{
    public async Task<ErrorOr<InvoiceQueueStatsDto>> Handle(
        GetQueueStatsQuery query,
        CancellationToken cancellationToken)
    {
        var stats = await context.InvoiceQueue
            .AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new InvoiceQueueStatsDto
            {
                TotalQueued = g.Count(),
                Pending = g.Count(q => q.Status == InvoiceQueueStatus.Pending),
                Processing = g.Count(q => q.Status == InvoiceQueueStatus.Processing),
                Completed = g.Count(q => q.Status == InvoiceQueueStatus.Completed),
                Failed = g.Count(q => q.Status == InvoiceQueueStatus.Failed),
                RequiresReview = g.Count(q => q.Status == InvoiceQueueStatus.RequiresReview),
                Cancelled = g.Count(q => q.Status == InvoiceQueueStatus.Cancelled),
                Fiscalized = g.Count(q => q.Status == InvoiceQueueStatus.Fiscalized),
                OldestPendingAge = g.Where(q => q.Status == InvoiceQueueStatus.Pending)
                    .Min(q => (DateTime?)q.CreatedAt),
                TotalAmountPending = g.Where(q => q.Status == InvoiceQueueStatus.Pending)
                    .Sum(q => q.TotalAmount)
            })
            .SingleOrDefaultAsync(cancellationToken);

        return stats ?? new InvoiceQueueStatsDto();
    }
}
