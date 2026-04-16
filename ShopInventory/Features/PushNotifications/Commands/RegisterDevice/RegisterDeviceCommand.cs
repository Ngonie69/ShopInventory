using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.PushNotifications.Commands.RegisterDevice;

public sealed record RegisterDeviceCommand(
    RegisterDeviceRequest Request,
    Guid UserId
) : IRequest<ErrorOr<DeviceRegistrationDto>>;
