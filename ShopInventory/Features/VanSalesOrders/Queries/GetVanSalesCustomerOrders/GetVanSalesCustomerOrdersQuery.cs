using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesOrders.Queries.GetVanSalesCustomerOrders;

/// <summary>
/// The signed-in shop's own order history, newest first.
/// </summary>
/// <remarks>
/// The account comes from the token. There is no customer parameter, because an endpoint that took
/// one would let any signed-in shop read another's trading.
/// </remarks>
public sealed record GetVanSalesCustomerOrdersQuery(
    int AccountId,
    int Page,
    int PageSize
) : IRequest<ErrorOr<VanSalesOrderListResult>>;
