using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.DesktopIntegration.Commands.ConfirmReservation;

public sealed record ConfirmReservationCommand(
    ConfirmReservationRequest Request
) : IRequest<ErrorOr<ConfirmReservationResponseDto>>;
