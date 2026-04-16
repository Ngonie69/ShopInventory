using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Payments.Commands.InitiatePayment;

public sealed class InitiatePaymentHandler(
    IPaymentGatewayService paymentService,
    IAuditService auditService,
    ILogger<InitiatePaymentHandler> logger
) : IRequestHandler<InitiatePaymentCommand, ErrorOr<InitiatePaymentResponse>>
{
    public async Task<ErrorOr<InitiatePaymentResponse>> Handle(
        InitiatePaymentCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await paymentService.InitiatePaymentAsync(command.Request, command.Username);
            try { await auditService.LogAsync(AuditActions.InitiatePayment, "Payment", null, $"Payment initiated via {command.Request.Provider} for {command.Request.Amount} {command.Request.Currency}", true); } catch { }
            return response;
        }
        catch (ArgumentException ex)
        {
            return Errors.Payment.InitiationFailed(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Errors.Payment.InitiationFailed(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error initiating payment");
            return Errors.Payment.InitiationFailed("Failed to initiate payment");
        }
    }
}
