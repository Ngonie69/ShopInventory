using ErrorOr;
using MediatR;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetTransfersRequiringReview;

public sealed record GetTransfersRequiringReviewQuery(
    int Limit = 50
) : IRequest<ErrorOr<List<InventoryTransferQueueStatusDto>>>;
