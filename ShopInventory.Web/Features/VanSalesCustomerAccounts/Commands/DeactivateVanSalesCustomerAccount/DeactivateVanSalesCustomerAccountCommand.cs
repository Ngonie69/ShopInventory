using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.VanSalesCustomerAccounts.Commands.DeactivateVanSalesCustomerAccount;

/// <summary>Withdraw a shop's sign-in and end the sessions it holds.</summary>
public sealed record DeactivateVanSalesCustomerAccountCommand(
    int AccountId
) : IRequest<ErrorOr<VanSalesCustomerAccountModel>>;
