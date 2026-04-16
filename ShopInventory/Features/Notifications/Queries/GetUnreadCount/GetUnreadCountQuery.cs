using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Notifications.Queries.GetUnreadCount;

public sealed record GetUnreadCountQuery(
    string? Username,
    string? Role
) : IRequest<ErrorOr<int>>;
