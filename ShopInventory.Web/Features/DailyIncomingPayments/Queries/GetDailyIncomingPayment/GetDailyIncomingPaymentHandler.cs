using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.DailyIncomingPayments.Queries.GetDailyIncomingPayment;

public sealed class GetDailyIncomingPaymentHandler(HttpClient httpClient, ILogger<GetDailyIncomingPaymentHandler> logger)
    : IRequestHandler<GetDailyIncomingPaymentQuery, ErrorOr<DailyIncomingPaymentDetail>>
{
    public Task<ErrorOr<DailyIncomingPaymentDetail>> Handle(GetDailyIncomingPaymentQuery request, CancellationToken cancellationToken) =>
        DailyIncomingPaymentsApi.SendAsync<DailyIncomingPaymentDetail>(
            httpClient,
            logger,
            HttpMethod.Get,
            $"{DailyIncomingPaymentsApi.Base}/{request.Id}",
            null,
            "load the payment's invoices",
            cancellationToken);
}
