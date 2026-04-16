using ErrorOr;
using MediatR;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetPendingTransferQueue;

public sealed record GetPendingTransferQueueQuery(
    string? SourceSystem = null,
    int Limit = 100
) : IRequest<ErrorOr<List<InventoryTransferQueueStatusDto>>>;
