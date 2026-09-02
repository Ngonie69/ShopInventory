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
/// <param name="Password">
/// What the shop will sign in with. Required for a new account; on one that already exists, blank
/// leaves the current password alone and anything else replaces it. Replacing it here is also how a
/// forgotten password is reset — the rep is standing in the shop confirming who this is, which is
/// the same check that justified creating the account in the first place.
/// </param>
public sealed record OnboardVanSalesCustomerAccountCommand(
    int RouteCustomerId,
    string? PhoneNumber,
    string? DisplayName,
    Guid? CreatedByUserId,
    string? Password = null
) : IRequest<ErrorOr<VanSalesCustomerAccountResult>>;
