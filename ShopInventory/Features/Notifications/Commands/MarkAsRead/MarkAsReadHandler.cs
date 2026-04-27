using ErrorOr;
using MediatR;
using ShopInventory.Services;

namespace ShopInventory.Features.Notifications.Commands.MarkAsRead;

public sealed class MarkAsReadHandler(
    INotificationService notificationService
) : IRequestHandler<MarkAsReadCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        MarkAsReadCommand command,
        CancellationToken cancellationToken)
    {
        await notificationService.MarkAsReadAsync(
            command.Username, command.Roles, command.NotificationIds, cancellationToken);
        return Result.Success;
    }
}
