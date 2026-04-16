using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.Payments.Commands.ProcessEcocashCallback;

public sealed class ProcessEcocashCallbackHandler(
    IPaymentGatewayService paymentService,
    ILogger<ProcessEcocashCallbackHandler> logger
) : IRequestHandler<ProcessEcocashCallbackCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        ProcessEcocashCallbackCommand command,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Processing Ecocash callback");
        command.Payload.Provider = "Ecocash";
        command.Payload.Signature ??= command.Signature;
        var result = await paymentService.ProcessCallbackAsync("Ecocash", command.Payload);
        return result ? Result.Success : Errors.Payment.CallbackFailed("Ecocash callback processing failed");
    }
}
