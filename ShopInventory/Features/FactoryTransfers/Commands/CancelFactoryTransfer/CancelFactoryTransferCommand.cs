using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.FactoryTransfers.Commands.CancelFactoryTransfer;

public sealed record CancelFactoryTransferCommand(
    int TransferId,
    Guid UserId) : IRequest<ErrorOr<FactoryTransferActionResultDto>>;
