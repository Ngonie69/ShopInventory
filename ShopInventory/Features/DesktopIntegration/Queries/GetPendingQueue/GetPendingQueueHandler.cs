using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetPendingQueue;

public sealed class GetPendingQueueHandler(
    IInvoiceQueueService queueService
) : IRequestHandler<GetPendingQueueQuery, ErrorOr<List<InvoiceQueueStatusDto>>>
{
    public async Task<ErrorOr<List<InvoiceQueueStatusDto>>> Handle(
        GetPendingQueueQuery query,
        CancellationToken cancellationToken)
    {
        var pending = await queueService.GetPendingInvoicesAsync(
            query.SourceSystem, query.Limit, cancellationToken);
        return pending;
    }
}
