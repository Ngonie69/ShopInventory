using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Webhooks.Queries.GetWebhooks;

public sealed class GetWebhooksHandler(
    IWebhookService webhookService
) : IRequestHandler<GetWebhooksQuery, ErrorOr<List<WebhookDto>>>
{
    public async Task<ErrorOr<List<WebhookDto>>> Handle(
        GetWebhooksQuery query,
        CancellationToken cancellationToken)
    {
        var webhooks = await webhookService.GetAllWebhooksAsync();
        return webhooks;
    }
}
