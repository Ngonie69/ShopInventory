using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.PushNotifications.Queries.GetMyDevices;

public sealed record GetMyDevicesQuery(
    Guid UserId
) : IRequest<ErrorOr<List<DeviceRegistrationDto>>>;
