using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Notifications.Commands.CreateNotification;

public sealed record CreateNotificationCommand(
    CreateNotificationRequest Request
) : IRequest<ErrorOr<NotificationDto>>;
