using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Webhooks.Commands.CreateWebhook;

public sealed record CreateWebhookCommand(
    CreateWebhookRequest Request
) : IRequest<ErrorOr<WebhookDto>>;
