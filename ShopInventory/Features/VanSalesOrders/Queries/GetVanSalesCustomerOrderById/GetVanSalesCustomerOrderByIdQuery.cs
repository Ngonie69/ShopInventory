using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesOrders.Queries.GetVanSalesCustomerOrderById;

/// <summary>One of the signed-in shop's own orders.</summary>
public sealed record GetVanSalesCustomerOrderByIdQuery(
    int AccountId,
    int OrderId
) : IRequest<ErrorOr<VanSalesOrderResult>>;
