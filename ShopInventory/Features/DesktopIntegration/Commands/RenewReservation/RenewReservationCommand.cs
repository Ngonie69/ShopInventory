using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.DesktopIntegration.Commands.RenewReservation;

public sealed record RenewReservationCommand(
    RenewReservationRequest Request
) : IRequest<ErrorOr<StockReservationResponseDto>>;
