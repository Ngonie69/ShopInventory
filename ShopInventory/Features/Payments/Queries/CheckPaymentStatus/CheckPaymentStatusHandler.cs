using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Payments.Queries.CheckPaymentStatus;

public sealed class CheckPaymentStatusHandler(
    IPaymentGatewayService paymentService
) : IRequestHandler<CheckPaymentStatusQuery, ErrorOr<PaymentStatusResponse>>
{
    public async Task<ErrorOr<PaymentStatusResponse>> Handle(
        CheckPaymentStatusQuery request,
        CancellationToken cancellationToken)
    {
        var status = await paymentService.CheckStatusAsync(request.Id);
        if (status is null)
        {
            return Errors.Payment.NotFound(request.Id);
        }
        return status;
    }
}
