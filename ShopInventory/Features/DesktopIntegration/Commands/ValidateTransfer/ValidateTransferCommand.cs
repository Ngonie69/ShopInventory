using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Commands.ValidateTransfer;

public sealed record ValidateTransferCommand(
    CreateDesktopTransferRequest Request
) : IRequest<ErrorOr<ValidateTransferResult>>;
