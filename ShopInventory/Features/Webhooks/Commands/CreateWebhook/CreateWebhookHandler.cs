using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Webhooks.Commands.CreateWebhook;

public sealed class CreateWebhookHandler(
    IWebhookService webhookService,
    ILogger<CreateWebhookHandler> logger
) : IRequestHandler<CreateWebhookCommand, ErrorOr<WebhookDto>>
{
    public async Task<ErrorOr<WebhookDto>> Handle(
        CreateWebhookCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var webhook = await webhookService.CreateWebhookAsync(command.Request);
            return webhook;
        }
        catch (ArgumentException ex)
        {
            return Errors.Webhook.CreationFailed(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating webhook");
            return Errors.Webhook.CreationFailed(ex.Message);
        }
    }
}
