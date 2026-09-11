using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.PushNotifications.Commands.RegisterDevice;

/// <summary>
/// Records a device against a staff account, and records that it was recorded.
/// </summary>
/// <remarks>
/// A registration decides which handset receives a person's notifications, and the device row shows
/// only the end state. Audited so that a device appearing against an account has a moment attached
/// to it.
/// </remarks>
public sealed class RegisterDeviceHandler(
    IPushNotificationService pushService,
    IAuditService auditService,
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

            await auditService.LogAsync(
                AuditActions.RegisterPushDevice,
                "PushDevice",
                command.UserId.ToString(),
                $"Registered {command.Request.Platform} device "
                    + $"{command.Request.DeviceName ?? "(unnamed)"}, app version "
                    + $"{command.Request.AppVersion ?? "(unstated)"}.",
                true,
                null);

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error registering device for user {UserId}", command.UserId);

            await auditService.LogAsync(
                AuditActions.RegisterPushDevice,
                "PushDevice",
                command.UserId.ToString(),
                "Registering a push device threw.",
                false,
                ex.Message);

            return Errors.PushNotification.RegistrationFailed(ex.Message);
        }
    }
}
