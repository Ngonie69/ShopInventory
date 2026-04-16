using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Webhooks.Queries.GetDeliveries;

public sealed class GetDeliveriesHandler(
    IWebhookService webhookService
) : IRequestHandler<GetDeliveriesQuery, ErrorOr<WebhookDeliveryListResponse>>
{
    public async Task<ErrorOr<WebhookDeliveryListResponse>> Handle(
        GetDeliveriesQuery query,
        CancellationToken cancellationToken)
    {
        var deliveries = await webhookService.GetDeliveriesAsync(
            query.WebhookId, query.Page, query.PageSize);
        return deliveries;
    }
}
