using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.InventoryTransfers.Commands.EditTransferRequest;

public sealed record EditTransferRequestCommand(
    int DocEntry,
    EditTransferRequestDto Request,
    Guid UserId) : IRequest<ErrorOr<TransferRequestEditResponseDto>>;
