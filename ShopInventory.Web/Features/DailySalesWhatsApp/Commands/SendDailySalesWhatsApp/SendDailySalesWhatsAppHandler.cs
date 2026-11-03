using ErrorOr;
using MediatR;
using ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesReview;

namespace ShopInventory.Web.Features.DailySalesWhatsApp.Commands.SendDailySalesWhatsApp;

public sealed class SendDailySalesWhatsAppHandler(HttpClient httpClient, ILogger<SendDailySalesWhatsAppHandler> logger)
    : IRequestHandler<SendDailySalesWhatsAppCommand, ErrorOr<DailySalesWhatsAppSendResult>>
{
    public Task<ErrorOr<DailySalesWhatsAppSendResult>> Handle(SendDailySalesWhatsAppCommand request, CancellationToken cancellationToken) =>
        DesktopSalesReviewApi.SendAsync<DailySalesWhatsAppSendResult>(
            httpClient, logger, HttpMethod.Post, DailySalesWhatsAppApi.Base + "/send",
            new { request.TradingDate, request.Recipients },
            "send the daily sales WhatsApp report", cancellationToken);
}
