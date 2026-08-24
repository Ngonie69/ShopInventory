using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;

namespace ShopInventory.Features.VanSalesOrders.Queries.GetVanSalesCustomerOrderById;

/// <summary>
/// Reads one order, provided it belongs to the caller.
/// </summary>
/// <remarks>
/// Ownership is part of the where clause, not a check on the result. Fetching first and comparing
/// afterwards is the same query with an extra chance to forget the comparison, and an order that is
/// not the caller's is reported as not found rather than forbidden — otherwise the two answers let
/// a customer walk the id range and count a competitor's orders.
/// </remarks>
public sealed class GetVanSalesCustomerOrderByIdHandler(ApplicationDbContext context)
    : IRequestHandler<GetVanSalesCustomerOrderByIdQuery, ErrorOr<VanSalesOrderResult>>
{
    public async Task<ErrorOr<VanSalesOrderResult>> Handle(
        GetVanSalesCustomerOrderByIdQuery query,
        CancellationToken cancellationToken)
    {
        var order = await context.VanSalesOrders
            .AsNoTracking()
            .Where(o => o.Id == query.OrderId && o.VanSalesCustomerAccountId == query.AccountId)
            .Select(VanSalesOrderProjection.ToResult)
            .FirstOrDefaultAsync(cancellationToken);

        return order is null ? Errors.VanSalesOrders.NotFound : order;
    }
}
