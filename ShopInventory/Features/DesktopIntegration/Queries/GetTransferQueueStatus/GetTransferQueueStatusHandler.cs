using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetTransferQueueStatus;

public sealed class GetTransferQueueStatusHandler(
    IInventoryTransferQueueService transferQueueService
) : IRequestHandler<GetTransferQueueStatusQuery, ErrorOr<InventoryTransferQueueStatusDto>>
{
    public async Task<ErrorOr<InventoryTransferQueueStatusDto>> Handle(
        GetTransferQueueStatusQuery query,
        CancellationToken cancellationToken)
    {
        var status = await transferQueueService.GetQueueStatusAsync(query.ExternalReference, cancellationToken);

        if (status == null)
            return Errors.DesktopIntegration.QueueNotFound(query.ExternalReference);

        return status;
    }
}
