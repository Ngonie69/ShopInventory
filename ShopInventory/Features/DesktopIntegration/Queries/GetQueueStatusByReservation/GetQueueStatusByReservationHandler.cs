using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetQueueStatusByReservation;

public sealed class GetQueueStatusByReservationHandler(
    IInvoiceQueueService queueService
) : IRequestHandler<GetQueueStatusByReservationQuery, ErrorOr<InvoiceQueueStatusDto>>
{
    public async Task<ErrorOr<InvoiceQueueStatusDto>> Handle(
        GetQueueStatusByReservationQuery query,
        CancellationToken cancellationToken)
    {
        var status = await queueService.GetQueueStatusByReservationAsync(query.ReservationId, cancellationToken);

        if (status == null)
            return Errors.DesktopIntegration.QueueNotFound(query.ReservationId);

        return status;
    }
}
