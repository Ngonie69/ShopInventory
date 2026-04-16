using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.PushNotifications.Commands.UnregisterDevice;

public sealed class UnregisterDeviceHandler(
    IPushNotificationService pushService,
    ILogger<UnregisterDeviceHandler> logger
) : IRequestHandler<UnregisterDeviceCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        UnregisterDeviceCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            await pushService.UnregisterDeviceAsync(command.UserId, command.DeviceToken, cancellationToken);
            return Result.Success;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error unregistering device for user {UserId}", command.UserId);
            return Errors.PushNotification.RegistrationFailed(ex.Message);
        }
    }
}
