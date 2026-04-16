using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetQueueStatusByReservation;

public sealed record GetQueueStatusByReservationQuery(
    string ReservationId
) : IRequest<ErrorOr<InvoiceQueueStatusDto>>;
