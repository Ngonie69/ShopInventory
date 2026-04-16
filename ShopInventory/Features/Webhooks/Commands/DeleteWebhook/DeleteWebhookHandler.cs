using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.Webhooks.Commands.DeleteWebhook;

public sealed class DeleteWebhookHandler(
    IWebhookService webhookService
) : IRequestHandler<DeleteWebhookCommand, ErrorOr<Deleted>>
{
    public async Task<ErrorOr<Deleted>> Handle(
        DeleteWebhookCommand command,
        CancellationToken cancellationToken)
    {
        var deleted = await webhookService.DeleteWebhookAsync(command.Id);
        if (!deleted)
        {
            return Errors.Webhook.NotFound(command.Id);
        }
        return Result.Deleted;
    }
}
