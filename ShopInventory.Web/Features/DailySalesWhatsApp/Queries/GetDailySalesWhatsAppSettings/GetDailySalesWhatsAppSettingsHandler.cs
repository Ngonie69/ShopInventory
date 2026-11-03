using ErrorOr;
using MediatR;
using ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesReview;

namespace ShopInventory.Web.Features.DailySalesWhatsApp.Queries.GetDailySalesWhatsAppSettings;

public sealed class GetDailySalesWhatsAppSettingsHandler(HttpClient httpClient, ILogger<GetDailySalesWhatsAppSettingsHandler> logger)
    : IRequestHandler<GetDailySalesWhatsAppSettingsQuery, ErrorOr<DailySalesWhatsAppSettings>>
{
    public Task<ErrorOr<DailySalesWhatsAppSettings>> Handle(GetDailySalesWhatsAppSettingsQuery request, CancellationToken cancellationToken) =>
        DesktopSalesReviewApi.SendAsync<DailySalesWhatsAppSettings>(
            httpClient, logger, HttpMethod.Get, DailySalesWhatsAppApi.Base + "/settings", null,
            "load the daily sales WhatsApp report settings", cancellationToken);
}
