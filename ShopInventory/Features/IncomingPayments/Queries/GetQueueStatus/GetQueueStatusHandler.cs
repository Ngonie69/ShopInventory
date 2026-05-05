using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.IncomingPayments.Queries.GetQueueStatus;

public sealed class GetQueueStatusHandler(
    IIncomingPaymentQueueService incomingPaymentQueueService
) : IRequestHandler<GetQueueStatusQuery, ErrorOr<IncomingPaymentQueueStatusDto>>
{
    public async Task<ErrorOr<IncomingPaymentQueueStatusDto>> Handle(
        GetQueueStatusQuery query,
        CancellationToken cancellationToken)
    {
        var status = await incomingPaymentQueueService.GetQueueStatusAsync(query.ExternalReference, cancellationToken);

        if (status == null)
        {
            return Errors.IncomingPayment.QueueNotFound(query.ExternalReference);
        }

        return status;
    }
}