using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetReservationByReference;

public sealed record GetReservationByReferenceQuery(
    string ExternalReferenceId
) : IRequest<ErrorOr<StockReservationDto>>;
