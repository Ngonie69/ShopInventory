using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesCustomerAuth.Queries.GetVanSalesCustomerAccounts;

/// <summary>
/// The customer sign-ins an operator can see, optionally narrowed to one route customer.
/// </summary>
public sealed record GetVanSalesCustomerAccountsQuery(
    int? RouteCustomerId,
    bool IncludeInactive
) : IRequest<ErrorOr<List<VanSalesCustomerAccountResult>>>;
