using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.DesktopIntegration.Commands.CancelReservation;

public sealed record CancelReservationCommand(
    CancelReservationRequest Request
) : IRequest<ErrorOr<StockReservationResponseDto>>;
