using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.PushNotifications.Commands.SendPushNotification;

/// <summary>
/// Sends a push, and records who was reached.
/// </summary>
/// <remarks>
/// The only endpoint on this API that puts a message on staff phones, and with neither a target
/// username nor a target role it goes to every device the company has. Audited with the audience and
/// the delivered count, because afterwards nothing at all says a broadcast happened.
/// </remarks>
public sealed class SendPushNotificationHandler(
    IPushNotificationService pushService,
    IAuditService auditService,
    ILogger<SendPushNotificationHandler> logger
) : IRequestHandler<SendPushNotificationCommand, ErrorOr<SendPushNotificationResult>>
{
    public async Task<ErrorOr<SendPushNotificationResult>> Handle(
        SendPushNotificationCommand command,
        CancellationToken cancellationToken)
    {
        var audience = command.Request switch
        {
            { TargetUsername: { Length: > 0 } user } => $"user {user}",
            { TargetRole: { Length: > 0 } role } => $"role {role}",
            _ => "every registered device"
        };

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

            await auditService.LogAsync(
                AuditActions.SendPushNotification,
                "PushNotification",
                null,
                $"Sent \"{request.Title}\" to {audience}: {sent} device(s) reached.",
                true,
                null);

            return new SendPushNotificationResult(sent, request.Title);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error sending push notification");

            await auditService.LogAsync(
                AuditActions.SendPushNotification,
                "PushNotification",
                null,
                $"Sending \"{command.Request.Title}\" to {audience} threw.",
                false,
                ex.Message);

            return Errors.PushNotification.SendFailed(ex.Message);
        }
    }
}
