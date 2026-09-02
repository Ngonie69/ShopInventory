using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesCustomerAuth.Commands.SignInVanSalesCustomer;

/// <summary>Exchange a phone number and its password for a session.</summary>
public sealed record SignInVanSalesCustomerCommand(
    string? PhoneNumber,
    string? Password,
    string? DeviceId,
    string? DeviceName,
    string? RequestedFromIp
) : IRequest<ErrorOr<VanSalesCustomerSessionResult>>;
