using ErrorOr;
using MediatR;

using ShopInventory.DTOs;

namespace ShopInventory.Features.InventoryTransfers.Commands.CloseTransferRequest;

public sealed record CloseTransferRequestCommand(
    int DocEntry,
    Guid UserId,
    Guid? StageId = null,
    string? Remarks = null) : IRequest<ErrorOr<TransferRequestDecisionResponseDto>>;
