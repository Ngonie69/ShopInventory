using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.InventoryTransfers.Commands.CreateInventoryTransfer;

public sealed record CreateInventoryTransferCommand(
    CreateInventoryTransferRequest Request
) : IRequest<ErrorOr<InventoryTransferCreatedResponseDto>>;
