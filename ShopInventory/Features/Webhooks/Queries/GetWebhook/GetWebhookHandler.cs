using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Webhooks.Queries.GetWebhook;

public sealed class GetWebhookHandler(
    IWebhookService webhookService
) : IRequestHandler<GetWebhookQuery, ErrorOr<WebhookDto>>
{
    public async Task<ErrorOr<WebhookDto>> Handle(
        GetWebhookQuery query,
        CancellationToken cancellationToken)
    {
        var webhook = await webhookService.GetWebhookByIdAsync(query.Id);
        if (webhook is null)
        {
            return Errors.Webhook.NotFound(query.Id);
        }
        return webhook;
    }
}
