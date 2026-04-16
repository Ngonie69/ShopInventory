using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.Notifications.Commands.DeleteNotification;

public sealed class DeleteNotificationHandler(
    INotificationService notificationService,
    ILogger<DeleteNotificationHandler> logger
) : IRequestHandler<DeleteNotificationCommand, ErrorOr<Deleted>>
{
    public async Task<ErrorOr<Deleted>> Handle(
        DeleteNotificationCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            await notificationService.DeleteNotificationAsync(command.Id, cancellationToken);
            return Result.Deleted;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting notification {Id}", command.Id);
            return Errors.Notification.DeleteFailed(ex.Message);
        }
    }
}
