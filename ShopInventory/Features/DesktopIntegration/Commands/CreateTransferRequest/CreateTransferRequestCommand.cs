using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.DesktopIntegration.Commands.CreateTransferRequest;

public sealed record CreateTransferRequestCommand(
    CreateDesktopTransferRequestDto Request,
    string? CreatedBy
) : IRequest<ErrorOr<InventoryTransferRequestDto>>;
