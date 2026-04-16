using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetQueueStatus;

public sealed class GetQueueStatusHandler(
    IInvoiceQueueService queueService
) : IRequestHandler<GetQueueStatusQuery, ErrorOr<InvoiceQueueStatusDto>>
{
    public async Task<ErrorOr<InvoiceQueueStatusDto>> Handle(
        GetQueueStatusQuery query,
        CancellationToken cancellationToken)
    {
        var status = await queueService.GetQueueStatusAsync(query.ExternalReference, cancellationToken);

        if (status == null)
            return Errors.DesktopIntegration.QueueNotFound(query.ExternalReference);

        return status;
    }
}
