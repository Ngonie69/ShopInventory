using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.PushNotifications.Commands.RegisterDevice;

public sealed class RegisterDeviceHandler(
    IPushNotificationService pushService,
    ILogger<RegisterDeviceHandler> logger
) : IRequestHandler<RegisterDeviceCommand, ErrorOr<DeviceRegistrationDto>>
{
    public async Task<ErrorOr<DeviceRegistrationDto>> Handle(
        RegisterDeviceCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await pushService.RegisterDeviceAsync(command.UserId, command.Request, cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error registering device for user {UserId}", command.UserId);
            return Errors.PushNotification.RegistrationFailed(ex.Message);
        }
    }
}
