using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesCustomerAuth.Commands.RefreshVanSalesCustomerSession;

/// <summary>Trade a refresh token for a new session, rotating the token in the process.</summary>
public sealed record RefreshVanSalesCustomerSessionCommand(
    string? RefreshToken,
    string? DeviceId,
    string? DeviceName,
    string? RequestedFromIp
) : IRequest<ErrorOr<VanSalesCustomerSessionResult>>;
