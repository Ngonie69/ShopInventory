using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.PushNotifications.Commands.SendPushNotification;

public sealed class SendPushNotificationHandler(
    IPushNotificationService pushService,
    ILogger<SendPushNotificationHandler> logger
) : IRequestHandler<SendPushNotificationCommand, ErrorOr<SendPushNotificationResult>>
{
    public async Task<ErrorOr<SendPushNotificationResult>> Handle(
        SendPushNotificationCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = command.Request;
            var data = request.Data ?? new Dictionary<string, string>();
            int sent;

            if (!string.IsNullOrEmpty(request.TargetUsername))
            {
                sent = await pushService.SendToUsernameAsync(request.TargetUsername, request.Title, request.Body, data, cancellationToken);
            }
            else if (!string.IsNullOrEmpty(request.TargetRole))
            {
                sent = await pushService.SendToRoleAsync(request.TargetRole, request.Title, request.Body, data, cancellationToken);
            }
            else
            {
                sent = await pushService.SendToAllAsync(request.Title, request.Body, data, cancellationToken);
            }

            return new SendPushNotificationResult(sent, request.Title);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error sending push notification");
            return Errors.PushNotification.SendFailed(ex.Message);
        }
    }
}
