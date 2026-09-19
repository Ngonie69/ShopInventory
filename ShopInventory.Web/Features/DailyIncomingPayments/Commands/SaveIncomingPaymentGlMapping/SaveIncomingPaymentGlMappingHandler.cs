using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.DailyIncomingPayments.Commands.SaveIncomingPaymentGlMapping;

public sealed class SaveIncomingPaymentGlMappingHandler(HttpClient httpClient, ILogger<SaveIncomingPaymentGlMappingHandler> logger)
    : IRequestHandler<SaveIncomingPaymentGlMappingCommand, ErrorOr<IncomingPaymentGlMapping>>
{
    public Task<ErrorOr<IncomingPaymentGlMapping>> Handle(SaveIncomingPaymentGlMappingCommand request, CancellationToken cancellationToken) =>
        DailyIncomingPaymentsApi.SendAsync<IncomingPaymentGlMapping>(
            httpClient,
            logger,
            HttpMethod.Put,
            $"{DailyIncomingPaymentsApi.Base}/gl-mappings/{Uri.EscapeDataString(request.CardCode.Trim())}",
            request.Body,
            "save the G/L accounts",
            cancellationToken);
}
