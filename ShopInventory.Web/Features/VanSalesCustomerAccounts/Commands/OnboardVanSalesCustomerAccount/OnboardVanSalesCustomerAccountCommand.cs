using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.VanSalesCustomerAccounts.Commands.OnboardVanSalesCustomerAccount;

/// <summary>Give a shop an ordering-app sign-in, or re-point an existing one at a new handset.</summary>
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
