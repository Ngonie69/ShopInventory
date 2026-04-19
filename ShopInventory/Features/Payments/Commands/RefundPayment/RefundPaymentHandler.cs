using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Payments.Commands.RefundPayment;

public sealed class RefundPaymentHandler(
    IPaymentGatewayService paymentService,
    IAuditService auditService
) : IRequestHandler<RefundPaymentCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        RefundPaymentCommand command,
        CancellationToken cancellationToken)
    {
        var result = await paymentService.RefundPaymentAsync(command.Id, command.Amount);
        if (!result)
        {
            return Errors.Payment.RefundFailed("Cannot refund this payment. It may not have been completed.");
        }
        try { await auditService.LogAsync(AuditActions.RefundPayment, "Payment", command.Id.ToString(), $"Payment {command.Id} refunded{(command.Amount.HasValue ? $" (amount: {command.Amount.Value})" : "")}", true); } catch { }
        return Result.Success;
    }
}
