using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Webhooks.Commands.UpdateWebhook;

public sealed class UpdateWebhookHandler(
    IWebhookService webhookService,
    ILogger<UpdateWebhookHandler> logger
) : IRequestHandler<UpdateWebhookCommand, ErrorOr<WebhookDto>>
{
    public async Task<ErrorOr<WebhookDto>> Handle(
        UpdateWebhookCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var webhook = await webhookService.UpdateWebhookAsync(command.Id, command.Request);
            if (webhook is null)
            {
                return Errors.Webhook.NotFound(command.Id);
            }
            return webhook;
        }
        catch (ArgumentException ex)
        {
            return Errors.Webhook.UpdateFailed(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating webhook {WebhookId}", command.Id);
            return Errors.Webhook.UpdateFailed(ex.Message);
        }
    }
}
