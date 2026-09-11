using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.PushNotifications.Commands.UnregisterDevice;

public sealed class UnregisterDeviceHandler(
    IPushNotificationService pushService,
    IAuditService auditService,
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

            await auditService.LogAsync(
                AuditActions.UnregisterPushDevice,
                "PushDevice",
                command.UserId.ToString(),
                "Unregistered a push device, so it stops receiving this account's notifications.",
                true,
                null);

            return Result.Success;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error unregistering device for user {UserId}", command.UserId);

            await auditService.LogAsync(
                AuditActions.UnregisterPushDevice,
                "PushDevice",
                command.UserId.ToString(),
                "Unregistering a push device threw.",
                false,
                ex.Message);

            return Errors.PushNotification.RegistrationFailed(ex.Message);
        }
    }
}
