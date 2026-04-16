using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Webhooks.Commands.UpdateWebhook;

public sealed record UpdateWebhookCommand(
    int Id,
    UpdateWebhookRequest Request
) : IRequest<ErrorOr<WebhookDto>>;
