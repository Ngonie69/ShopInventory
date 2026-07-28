using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.InventoryTransfers.Queries.GetPendingTransferById;

public sealed record GetPendingTransferByIdQuery(
    Guid PendingTransferId,
    Guid UserId) : IRequest<ErrorOr<PendingInventoryTransferDto>>;
