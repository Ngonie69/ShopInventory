using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Notifications.Queries.GetNotifications;

public sealed class GetNotificationsHandler(
    INotificationService notificationService
) : IRequestHandler<GetNotificationsQuery, ErrorOr<NotificationListResponseDto>>
{
    public async Task<ErrorOr<NotificationListResponseDto>> Handle(
        GetNotificationsQuery request,
        CancellationToken cancellationToken)
    {
        var notifications = await notificationService.GetNotificationsAsync(
            request.Username, request.Roles, request.Page, request.PageSize,
            request.UnreadOnly, request.Category, cancellationToken);
        return notifications;
    }
}
