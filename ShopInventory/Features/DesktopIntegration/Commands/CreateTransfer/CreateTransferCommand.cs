using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Commands.CreateTransfer;

public sealed record CreateTransferCommand(
    CreateDesktopTransferRequest Request
) : IRequest<ErrorOr<InventoryTransferCreatedResponseDto>>;
