using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Notifications.Queries.GetNotifications;

public sealed record GetNotificationsQuery(
    int Page,
    int PageSize,
    bool UnreadOnly,
    string? Category,
    string? Username,
    IReadOnlyList<string> Roles
) : IRequest<ErrorOr<NotificationListResponseDto>>;
