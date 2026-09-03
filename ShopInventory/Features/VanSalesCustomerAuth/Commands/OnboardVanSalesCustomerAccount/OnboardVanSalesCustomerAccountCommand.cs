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
/// <param name="RouteCustomerId">
/// The shop whose orders this sign-in will place. It has to exist and still be active, and it is
/// settled once the account is made: sending the same phone in against a different customer is
/// refused rather than honoured, because obeying it would move the first shop's orders onto a
/// handset that is not theirs.
/// </param>
/// <param name="PhoneNumber">
/// The number the shop signs in with, as the rep typed it — a leading '+', spaces, dashes, dots and
/// brackets are all read as formatting and normalised to E.164 against the configured default
/// country code, while a letter in the middle is refused rather than guessed at. It is the
/// normalised form that identifies the account, so the same number written two ways re-points the
/// sign-in that already exists instead of quietly opening a second one.
/// </param>
/// <param name="DisplayName">
/// What to call the person holding the phone; the shop's own name is already on the route customer,
/// so this is only worth setting where the two differ. Optional, and leaving it null on an account
/// that already exists keeps the name it has.
/// </param>
/// <param name="CreatedByUserId">
/// The staff member who set the account up, stamped once when it is created and left alone by every
/// later onboarding — it records who vouched for this shop in the first place, not who touched the
/// account most recently. Null where the caller is not a signed-in user.
/// </param>
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
