using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Payments.Commands.CancelPayment;

public sealed class CancelPaymentHandler(
    IPaymentGatewayService paymentService,
    IAuditService auditService,
    ILogger<CancelPaymentHandler> logger
) : IRequestHandler<CancelPaymentCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        CancelPaymentCommand command,
        CancellationToken cancellationToken)
    {
        var result = await paymentService.CancelPaymentAsync(command.Id);
        if (!result)
        {
            return Errors.Payment.CancellationFailed("Cannot cancel this payment. It may have already been processed.");
        }
        try { await auditService.LogAsync(AuditActions.CancelPayment, "Payment", command.Id.ToString(), $"Payment {command.Id} cancelled", true); } catch { }
        return Result.Success;
    }
}
