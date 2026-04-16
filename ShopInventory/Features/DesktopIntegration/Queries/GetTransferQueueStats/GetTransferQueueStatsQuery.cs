using ErrorOr;
using MediatR;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetTransferQueueStats;

public sealed record GetTransferQueueStatsQuery() : IRequest<ErrorOr<InventoryTransferQueueStatsDto>>;
