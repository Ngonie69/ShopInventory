using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;

namespace ShopInventory.Features.VanSalesOrders.Queries.GetVanSalesCustomerOrderByClientRequestId;

/// <summary>
/// Resolves the order a handset's idempotency key created, if any.
/// </summary>
/// <remarks>
/// Scoped to the caller's own account as well as the key. The key is a client-generated GUID and
/// guessing one is not a realistic attack, but an endpoint that returned any order for any key
/// would still be one an authenticated customer could fish in — and the scope costs nothing.
/// <para>
/// Not-found here means "no order was created", which is the answer that tells the app it is safe
/// to send again. That makes it the one lookup where a false negative is dangerous, so it queries
/// the key directly rather than anything derived from it.
/// </para>
/// </remarks>
public sealed class GetVanSalesCustomerOrderByClientRequestIdHandler(ApplicationDbContext context)
    : IRequestHandler<GetVanSalesCustomerOrderByClientRequestIdQuery, ErrorOr<VanSalesOrderResult>>
{
    public async Task<ErrorOr<VanSalesOrderResult>> Handle(
        GetVanSalesCustomerOrderByClientRequestIdQuery query,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query.ClientRequestId))
        {
            return Errors.VanSalesOrders.NotFound;
        }

        var clientRequestId = query.ClientRequestId.Trim();

        var order = await context.VanSalesOrders
            .AsNoTracking()
            .Where(o => o.ClientRequestId == clientRequestId
                        && o.VanSalesCustomerAccountId == query.AccountId)
            .Select(VanSalesOrderProjection.ToResult)
            .FirstOrDefaultAsync(cancellationToken);

        return order is null ? Errors.VanSalesOrders.NotFound : order;
    }
}
