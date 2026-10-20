using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.FactoryTransfers.Commands.RecallFactoryTransfer;

public sealed record RecallFactoryTransferCommand(
    int TransferId,
    string Reason,
    Guid UserId) : IRequest<ErrorOr<FactoryTransferActionResultDto>>;
