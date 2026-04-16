using ShopInventory.DTOs;
using ShopInventory.Services;
using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Commands.CreateQueuedTransfer;

public sealed record CreateQueuedTransferCommand(
    CreateDesktopTransferRequest Request,
    string? CreatedBy
) : IRequest<ErrorOr<QueuedTransferResponseDto>>;
