using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.VanSalesCustomerAccounts.Commands.OnboardVanSalesCustomerAccount;

/// <summary>Give a shop an ordering-app sign-in, or re-point an existing one at a new handset.</summary>
public sealed record OnboardVanSalesCustomerAccountCommand(
    int RouteCustomerId,
    string PhoneNumber,
    string? DisplayName
) : IRequest<ErrorOr<VanSalesCustomerAccountModel>>;
