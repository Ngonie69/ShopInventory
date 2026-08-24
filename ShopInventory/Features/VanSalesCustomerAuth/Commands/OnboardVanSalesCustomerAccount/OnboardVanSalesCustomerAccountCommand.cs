using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesCustomerAuth.Commands.OnboardVanSalesCustomerAccount;

/// <summary>
/// Give a van sales customer a sign-in for the ordering app, or point an existing one at a new
/// phone.
/// </summary>
/// <remarks>
/// Staff-initiated by design. There is no self-registration: a customer who could sign themselves
/// up could place orders as a shop they do not own, and the rep visiting the shop is the only party
/// in a position to confirm that the person holding the phone is the person who runs it.
/// </remarks>
public sealed record OnboardVanSalesCustomerAccountCommand(
    int RouteCustomerId,
    string? PhoneNumber,
    string? DisplayName,
    Guid? CreatedByUserId
) : IRequest<ErrorOr<VanSalesCustomerAccountResult>>;
