using ErrorOr;
using MediatR;
using ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesReview;

namespace ShopInventory.Web.Features.DailySalesWhatsApp.Queries.PreviewDailySalesWhatsApp;

public sealed class PreviewDailySalesWhatsAppHandler(HttpClient httpClient, ILogger<PreviewDailySalesWhatsAppHandler> logger)
    : IRequestHandler<PreviewDailySalesWhatsAppQuery, ErrorOr<DailySalesWhatsAppPreview>>
{
    public Task<ErrorOr<DailySalesWhatsAppPreview>> Handle(PreviewDailySalesWhatsAppQuery request, CancellationToken cancellationToken) =>
        DesktopSalesReviewApi.SendAsync<DailySalesWhatsAppPreview>(
            httpClient, logger, HttpMethod.Get,
            DailySalesWhatsAppApi.Base + "/preview" + (request.TradingDate is { } day ? $"?date={day:yyyy-MM-dd}" : string.Empty),
            null, "preview the daily sales WhatsApp report", cancellationToken);
}
