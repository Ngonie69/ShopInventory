using ErrorOr;
using MediatR;
using ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesReview;

namespace ShopInventory.Web.Features.DailySalesWhatsApp.Commands.UpdateDailySalesWhatsAppSettings;

public sealed class UpdateDailySalesWhatsAppSettingsHandler(HttpClient httpClient, ILogger<UpdateDailySalesWhatsAppSettingsHandler> logger)
    : IRequestHandler<UpdateDailySalesWhatsAppSettingsCommand, ErrorOr<DailySalesWhatsAppSettings>>
{
    public Task<ErrorOr<DailySalesWhatsAppSettings>> Handle(UpdateDailySalesWhatsAppSettingsCommand request, CancellationToken cancellationToken) =>
        DesktopSalesReviewApi.SendAsync<DailySalesWhatsAppSettings>(
            httpClient, logger, HttpMethod.Put, DailySalesWhatsAppApi.Base + "/settings",
            new
            {
                request.Enabled,
                request.Recipients,
                request.ShopWarehouseCode,
                request.ShopHeading,
                request.VanRows,
                request.ProductRows
            },
            "save the daily sales WhatsApp report settings", cancellationToken);
}
