using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.DailyIncomingPayments.Queries.GetDailyIncomingPayments;

public sealed class GetDailyIncomingPaymentsHandler(HttpClient httpClient, ILogger<GetDailyIncomingPaymentsHandler> logger)
    : IRequestHandler<GetDailyIncomingPaymentsQuery, ErrorOr<List<DailyIncomingPaymentSummary>>>
{
    public Task<ErrorOr<List<DailyIncomingPaymentSummary>>> Handle(GetDailyIncomingPaymentsQuery request, CancellationToken cancellationToken) =>
        DailyIncomingPaymentsApi.SendAsync<List<DailyIncomingPaymentSummary>>(
            httpClient,
            logger,
            HttpMethod.Get,
            DailyIncomingPaymentsApi.Base + $"?from={request.From:yyyy-MM-dd}&to={request.To:yyyy-MM-dd}"
                + (string.IsNullOrWhiteSpace(request.CardCode) ? "" : "&cardCode=" + Uri.EscapeDataString(request.CardCode.Trim()))
                + (string.IsNullOrWhiteSpace(request.Status) ? "" : "&status=" + Uri.EscapeDataString(request.Status)),
            null,
            "load the daily incoming payments",
            cancellationToken);
}
