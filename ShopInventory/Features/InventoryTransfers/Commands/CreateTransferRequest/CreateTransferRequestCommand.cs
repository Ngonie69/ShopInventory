using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.InventoryTransfers.Commands.CreateTransferRequest;

public sealed record CreateTransferRequestCommand(
    CreateTransferRequestDto Request,
    Guid UserId
) : IRequest<ErrorOr<TransferRequestCreatedResponseDto>>;
