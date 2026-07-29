using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.InventoryTransfers.Commands.DecidePendingRequestEdit;

public sealed record DecidePendingRequestEditCommand(
    Guid PendingEditId,
    Guid UserId,
    string Decision,
    Guid? StageId,
    string? Remarks) : IRequest<ErrorOr<PendingTransferRequestEditDecisionResponseDto>>;

public sealed record CancelPendingRequestEditCommand(
    Guid PendingEditId,
    Guid UserId) : IRequest<ErrorOr<PendingTransferRequestEditDecisionResponseDto>>;
