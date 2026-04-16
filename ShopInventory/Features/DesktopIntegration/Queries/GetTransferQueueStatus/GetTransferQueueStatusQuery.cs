using ErrorOr;
using MediatR;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetTransferQueueStatus;

public sealed record GetTransferQueueStatusQuery(
    string ExternalReference
) : IRequest<ErrorOr<InventoryTransferQueueStatusDto>>;
