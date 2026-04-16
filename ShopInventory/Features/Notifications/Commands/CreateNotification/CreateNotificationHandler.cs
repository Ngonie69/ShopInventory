using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Notifications.Commands.CreateNotification;

public sealed class CreateNotificationHandler(
    INotificationService notificationService,
    ILogger<CreateNotificationHandler> logger
) : IRequestHandler<CreateNotificationCommand, ErrorOr<NotificationDto>>
{
    public async Task<ErrorOr<NotificationDto>> Handle(
        CreateNotificationCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var notification = await notificationService.CreateNotificationAsync(
                command.Request, cancellationToken);
            return notification;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating notification");
            return Errors.Notification.CreationFailed(ex.Message);
        }
    }
}
