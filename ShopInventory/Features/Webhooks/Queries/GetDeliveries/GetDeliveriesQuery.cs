using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Webhooks.Queries.GetDeliveries;

public sealed record GetDeliveriesQuery(
    int? WebhookId,
    int Page,
    int PageSize
) : IRequest<ErrorOr<WebhookDeliveryListResponse>>;
