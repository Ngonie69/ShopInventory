using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;
using System.Globalization;

namespace ShopInventory.Features.Payments.Commands.ProcessPayNowCallback;

public sealed class ProcessPayNowCallbackHandler(
    IPaymentGatewayService paymentService,
    ILogger<ProcessPayNowCallbackHandler> logger
) : IRequestHandler<ProcessPayNowCallbackCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        ProcessPayNowCallbackCommand command,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Processing PayNow callback");

        var form = command.FormData;
        decimal? amount = null;
        if (form.TryGetValue("amount", out var amountStr) &&
            decimal.TryParse(amountStr, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedAmount))
        {
            amount = parsedAmount;
        }

        var payload = new PaymentCallbackPayload
        {
            Provider = "PayNow",
            TransactionId = form.GetValueOrDefault("paynowreference"),
            ExternalTransactionId = form.GetValueOrDefault("pollurl"),
            Status = form.GetValueOrDefault("status"),
            Reference = form.GetValueOrDefault("reference"),
            Amount = amount,
            Signature = form.GetValueOrDefault("hash"),
            RawData = new Dictionary<string, object>(form.Select(kv =>
                new KeyValuePair<string, object>(kv.Key, kv.Value)), StringComparer.OrdinalIgnoreCase)
        };

        var result = await paymentService.ProcessCallbackAsync("PayNow", payload);
        return result ? Result.Success : Errors.Payment.CallbackFailed("PayNow callback processing failed");
    }
}
