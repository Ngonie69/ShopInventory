using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetQueueStats;

public sealed class GetQueueStatsHandler(
    IInvoiceQueueService queueService
) : IRequestHandler<GetQueueStatsQuery, ErrorOr<InvoiceQueueStatsDto>>
{
    public async Task<ErrorOr<InvoiceQueueStatsDto>> Handle(
        GetQueueStatsQuery query,
        CancellationToken cancellationToken)
    {
        var stats = await queueService.GetQueueStatsAsync(cancellationToken);
        return stats;
    }
}
