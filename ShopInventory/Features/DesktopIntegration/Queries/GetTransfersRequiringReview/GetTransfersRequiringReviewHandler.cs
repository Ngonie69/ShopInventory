using ErrorOr;
using MediatR;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetTransfersRequiringReview;

public sealed class GetTransfersRequiringReviewHandler(
    IInventoryTransferQueueService transferQueueService
) : IRequestHandler<GetTransfersRequiringReviewQuery, ErrorOr<List<InventoryTransferQueueStatusDto>>>
{
    public async Task<ErrorOr<List<InventoryTransferQueueStatusDto>>> Handle(
        GetTransfersRequiringReviewQuery query,
        CancellationToken cancellationToken)
    {
        var transfers = await transferQueueService.GetTransfersRequiringReviewAsync(
            query.Limit, cancellationToken);

        return transfers;
    }
}
