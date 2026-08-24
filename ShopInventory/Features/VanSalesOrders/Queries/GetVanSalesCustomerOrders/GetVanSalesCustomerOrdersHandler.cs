using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;

namespace ShopInventory.Features.VanSalesOrders.Queries.GetVanSalesCustomerOrders;

/// <summary>
/// Reads a customer's own orders, most recent first.
/// </summary>
/// <remarks>
/// Ordered by when the server received the order rather than when the handset says it was sent: a
/// queued order carries a device clock that may be days out or simply wrong, and a history that
/// reorders itself as offline orders arrive is a history nobody trusts.
/// </remarks>
public sealed class GetVanSalesCustomerOrdersHandler(ApplicationDbContext context)
    : IRequestHandler<GetVanSalesCustomerOrdersQuery, ErrorOr<VanSalesOrderListResult>>
{
    private const int MaxPageSize = 100;

    public async Task<ErrorOr<VanSalesOrderListResult>> Handle(
        GetVanSalesCustomerOrdersQuery query,
        CancellationToken cancellationToken)
    {
        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize is < 1 or > MaxPageSize ? 20 : query.PageSize;

        var orders = context.VanSalesOrders
            .AsNoTracking()
            .Where(o => o.VanSalesCustomerAccountId == query.AccountId);

        var totalCount = await orders.CountAsync(cancellationToken);

        var page1 = await orders
            .OrderByDescending(o => o.ReceivedAtUtc)
            .ThenByDescending(o => o.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(VanSalesOrderProjection.ToResult)
            .ToListAsync(cancellationToken);

        return new VanSalesOrderListResult(totalCount, page, pageSize, page1);
    }
}
