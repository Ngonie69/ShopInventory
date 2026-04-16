using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Notifications.Commands.MarkAsRead;

public sealed record MarkAsReadCommand(
    List<int> NotificationIds,
    string? Username
) : IRequest<ErrorOr<Success>>;
