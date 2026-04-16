using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Webhooks.Queries.GetWebhook;

public sealed record GetWebhookQuery(int Id) : IRequest<ErrorOr<WebhookDto>>;
