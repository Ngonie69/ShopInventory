using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Notifications.Commands.MarkAsRead;

public sealed record MarkAsReadCommand(
    List<int> NotificationIds,
    string? Username,
    IReadOnlyList<string> Roles
) : IRequest<ErrorOr<Success>>;
