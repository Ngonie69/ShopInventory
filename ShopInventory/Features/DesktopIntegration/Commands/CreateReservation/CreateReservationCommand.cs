using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.DesktopIntegration.Commands.CreateReservation;

public sealed record CreateReservationCommand(
    CreateStockReservationRequest Request,
    string? CreatedBy
) : IRequest<ErrorOr<StockReservationResponseDto>>;
