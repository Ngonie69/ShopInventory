using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesCustomerAuth.Commands.LogoutVanSalesCustomer;

/// <summary>End a customer's session on this device.</summary>
public sealed record LogoutVanSalesCustomerCommand(
    int AccountId,
    string? RefreshToken,
    string? DeviceId
) : IRequest<ErrorOr<Success>>;
