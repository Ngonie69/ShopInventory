using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.DailyIncomingPayments.Commands.SetIncomingPaymentGlMappingActive;

public sealed class SetIncomingPaymentGlMappingActiveHandler(HttpClient httpClient, ILogger<SetIncomingPaymentGlMappingActiveHandler> logger)
    : IRequestHandler<SetIncomingPaymentGlMappingActiveCommand, ErrorOr<IncomingPaymentGlMapping>>
{
    public Task<ErrorOr<IncomingPaymentGlMapping>> Handle(SetIncomingPaymentGlMappingActiveCommand request, CancellationToken cancellationToken) =>
        DailyIncomingPaymentsApi.SendAsync<IncomingPaymentGlMapping>(
            httpClient,
            logger,
            HttpMethod.Put,
            $"{DailyIncomingPaymentsApi.Base}/gl-mappings/{Uri.EscapeDataString(request.CardCode.Trim())}/active",
            new { isActive = request.IsActive },
            request.IsActive ? "switch the partner's daily payment on" : "switch the partner's daily payment off",
            cancellationToken);
}
