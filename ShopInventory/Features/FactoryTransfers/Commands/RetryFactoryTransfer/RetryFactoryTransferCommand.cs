using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.FactoryTransfers.Commands.RetryFactoryTransfer;

public sealed record RetryFactoryTransferCommand(
    int TransferId,
    Guid UserId) : IRequest<ErrorOr<FactoryTransferActionResultDto>>;
