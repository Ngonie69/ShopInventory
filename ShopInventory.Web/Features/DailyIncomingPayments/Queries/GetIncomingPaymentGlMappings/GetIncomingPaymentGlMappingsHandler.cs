using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.DailyIncomingPayments.Queries.GetIncomingPaymentGlMappings;

public sealed class GetIncomingPaymentGlMappingsHandler(HttpClient httpClient, ILogger<GetIncomingPaymentGlMappingsHandler> logger)
    : IRequestHandler<GetIncomingPaymentGlMappingsQuery, ErrorOr<List<IncomingPaymentGlMapping>>>
{
    public Task<ErrorOr<List<IncomingPaymentGlMapping>>> Handle(GetIncomingPaymentGlMappingsQuery request, CancellationToken cancellationToken) =>
        DailyIncomingPaymentsApi.SendAsync<List<IncomingPaymentGlMapping>>(
            httpClient,
            logger,
            HttpMethod.Get,
            DailyIncomingPaymentsApi.Base + "/gl-mappings",
            null,
            "load the G/L accounts",
            cancellationToken);
}
