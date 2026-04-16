using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Webhooks.Queries.GetEventTypes;

public sealed class GetEventTypesHandler(
    IWebhookService webhookService
) : IRequestHandler<GetEventTypesQuery, ErrorOr<WebhookEventTypesResponse>>
{
    public async Task<ErrorOr<WebhookEventTypesResponse>> Handle(
        GetEventTypesQuery query,
        CancellationToken cancellationToken)
    {
        var eventTypes = await webhookService.GetEventTypesAsync();
        return new WebhookEventTypesResponse { EventTypes = eventTypes };
    }
}
