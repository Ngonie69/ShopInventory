using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesOrders.Commands.RegisterVanSalesCustomerDevice;

/// <summary>Register, or refresh, a customer handset's push token.</summary>
/// <remarks>
/// Called on every sign-in and whenever Firebase rotates the token, which it does on reinstall and
/// occasionally on its own. So it has to be idempotent on the token rather than create a row each
/// time — otherwise a shopkeeper who reinstalls twice gets three copies of every notification.
/// </remarks>
public sealed record RegisterVanSalesCustomerDeviceCommand(
    int AccountId,
    string? DeviceToken,
    string? DeviceId,
    string? DeviceName,
    string? AppVersion
) : IRequest<ErrorOr<Success>>;
