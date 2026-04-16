using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Webhooks.Commands.TestWebhook;

public sealed class TestWebhookHandler(
    IWebhookService webhookService,
    ILogger<TestWebhookHandler> logger
) : IRequestHandler<TestWebhookCommand, ErrorOr<TestWebhookResponse>>
{
    public async Task<ErrorOr<TestWebhookResponse>> Handle(
        TestWebhookCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await webhookService.TestWebhookAsync(command.Id, command.Request);
            if (result.ErrorMessage == "Webhook not found")
            {
                return Errors.Webhook.NotFound(command.Id);
            }
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error testing webhook {WebhookId}", command.Id);
            return Errors.Webhook.TestFailed(ex.Message);
        }
    }
}
