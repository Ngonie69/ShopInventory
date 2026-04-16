using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.DesktopIntegration.Commands.ConvertTransferRequest;

public sealed record ConvertTransferRequestCommand(
    int DocEntry
) : IRequest<ErrorOr<InventoryTransferCreatedResponseDto>>;
