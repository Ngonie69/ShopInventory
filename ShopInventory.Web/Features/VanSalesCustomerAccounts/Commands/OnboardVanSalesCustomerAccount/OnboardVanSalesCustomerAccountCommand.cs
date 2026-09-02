using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.VanSalesCustomerAccounts.Commands.OnboardVanSalesCustomerAccount;

/// <summary>Give a shop an ordering-app sign-in, or re-point an existing one at a new handset.</summary>
/// <remarks>
/// Carries no operator id, unlike the API command it feeds. The API takes that off the
/// authenticated caller; a copy sent up from here would be one the API had no reason to believe.
/// </remarks>
/// <param name="RouteCustomerId">
/// The shop the sign-in belongs to. The screen refuses a zero before sending so the operator hears
/// about an unpicked shop without waiting on a round trip, but whether the shop exists and is still
/// active is the API's call and is not second-guessed here.
/// </param>
/// <param name="PhoneNumber">
/// The handset number, in whatever shape the operator typed it — trimmed on the way out and
/// otherwise passed along, because normalising it is the API's job and a second attempt here would
/// be a second answer to disagree with.
/// </param>
/// <param name="DisplayName">
/// What to call the person holding the phone. Blank is sent as nothing at all, which the API reads
/// as "leave the name this sign-in already has".
/// </param>
/// <param name="Password">
/// What the shop signs in with. Blank leaves an existing sign-in's password alone; the API refuses
/// a brand new sign-in that has none.
/// </param>
public sealed record OnboardVanSalesCustomerAccountCommand(
    int RouteCustomerId,
    string PhoneNumber,
    string? DisplayName,
    string? Password = null
) : IRequest<ErrorOr<VanSalesCustomerAccountModel>>;
