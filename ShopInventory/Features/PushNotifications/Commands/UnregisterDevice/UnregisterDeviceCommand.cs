using ErrorOr;
using MediatR;

namespace ShopInventory.Features.PushNotifications.Commands.UnregisterDevice;

public sealed record UnregisterDeviceCommand(
    string DeviceToken,
    Guid UserId
) : IRequest<ErrorOr<Success>>;
