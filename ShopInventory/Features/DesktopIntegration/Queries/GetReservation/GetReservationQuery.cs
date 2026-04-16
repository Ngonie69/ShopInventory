using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetReservation;

public sealed record GetReservationQuery(
    string ReservationId
) : IRequest<ErrorOr<StockReservationDto>>;
