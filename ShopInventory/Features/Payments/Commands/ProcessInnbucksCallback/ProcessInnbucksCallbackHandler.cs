using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.Payments.Commands.ProcessInnbucksCallback;

public sealed class ProcessInnbucksCallbackHandler(
    IPaymentGatewayService paymentService,
    ILogger<ProcessInnbucksCallbackHandler> logger
) : IRequestHandler<ProcessInnbucksCallbackCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        ProcessInnbucksCallbackCommand command,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Processing Innbucks callback");
        command.Payload.Provider = "Innbucks";
        command.Payload.Signature ??= command.Signature;
        var result = await paymentService.ProcessCallbackAsync("Innbucks", command.Payload);
        return result ? Result.Success : Errors.Payment.CallbackFailed("Innbucks callback processing failed");
    }
}
