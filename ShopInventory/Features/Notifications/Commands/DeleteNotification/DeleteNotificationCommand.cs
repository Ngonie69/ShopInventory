using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Notifications.Commands.DeleteNotification;

public sealed record DeleteNotificationCommand(
    int Id,
    string? Username,
    IReadOnlyCollection<string> Roles
) : IRequest<ErrorOr<Deleted>>;
