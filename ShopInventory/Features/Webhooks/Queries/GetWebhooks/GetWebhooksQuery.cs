using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Webhooks.Queries.GetWebhooks;

public sealed record GetWebhooksQuery() : IRequest<ErrorOr<List<WebhookDto>>>;
