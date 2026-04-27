using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Notifications.Queries.GetUnreadCount;

public sealed record GetUnreadCountQuery(
    string? Username,
    IReadOnlyList<string> Roles
) : IRequest<ErrorOr<int>>;
