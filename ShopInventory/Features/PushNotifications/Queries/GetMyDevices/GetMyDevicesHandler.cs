using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.PushNotifications.Queries.GetMyDevices;

public sealed class GetMyDevicesHandler(
    IPushNotificationService pushService,
    ILogger<GetMyDevicesHandler> logger
) : IRequestHandler<GetMyDevicesQuery, ErrorOr<List<DeviceRegistrationDto>>>
{
    public async Task<ErrorOr<List<DeviceRegistrationDto>>> Handle(
        GetMyDevicesQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var devices = await pushService.GetUserDevicesAsync(query.UserId, cancellationToken);
            return devices;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting devices for user {UserId}", query.UserId);
            return Errors.PushNotification.SendFailed(ex.Message);
        }
    }
}
