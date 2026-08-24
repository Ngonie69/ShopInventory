using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesCustomerAuth.Commands.RequestVanSalesCustomerOtp;

/// <summary>Ask for a sign-in code to be sent to a customer's phone.</summary>
public sealed record RequestVanSalesCustomerOtpCommand(
    string? PhoneNumber,
    string? RequestedFromIp
) : IRequest<ErrorOr<RequestVanSalesCustomerOtpResult>>;
