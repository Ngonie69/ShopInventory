using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesCustomerAuth.Commands.VerifyVanSalesCustomerOtp;

/// <summary>Exchange a phone number and the code sent to it for a session.</summary>
public sealed record VerifyVanSalesCustomerOtpCommand(
    string? PhoneNumber,
    string? Code,
    string? DeviceId,
    string? DeviceName,
    string? RequestedFromIp
) : IRequest<ErrorOr<VanSalesCustomerSessionResult>>;
