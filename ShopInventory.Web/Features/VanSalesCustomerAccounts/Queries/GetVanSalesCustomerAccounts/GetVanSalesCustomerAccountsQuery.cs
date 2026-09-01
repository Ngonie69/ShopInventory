using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.VanSalesCustomerAccounts.Queries.GetVanSalesCustomerAccounts;

/// <summary>Everything the accounts screen renders: the sign-ins, and the shops one can be given to.</summary>
public sealed record GetVanSalesCustomerAccountsQuery(
    int? RouteCustomerId,
    bool IncludeInactive
) : IRequest<ErrorOr<VanSalesCustomerAccountsViewModel>>;
