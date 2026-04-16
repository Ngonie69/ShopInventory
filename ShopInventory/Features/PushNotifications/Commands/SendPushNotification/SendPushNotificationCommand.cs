using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.PushNotifications.Commands.SendPushNotification;

public sealed record SendPushNotificationCommand(
    SendPushNotificationRequest Request
) : IRequest<ErrorOr<SendPushNotificationResult>>;

public sealed record SendPushNotificationResult(
    int Sent,
    string Title
);
