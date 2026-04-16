using ErrorOr;
using MediatR;
using ShopInventory.Services;

namespace ShopInventory.Features.Notifications.Queries.GetUnreadCount;

public sealed class GetUnreadCountHandler(
    INotificationService notificationService
) : IRequestHandler<GetUnreadCountQuery, ErrorOr<int>>
{
    public async Task<ErrorOr<int>> Handle(
        GetUnreadCountQuery request,
        CancellationToken cancellationToken)
    {
        var count = await notificationService.GetUnreadCountAsync(
            request.Username, request.Role, cancellationToken);
        return count;
    }
}
