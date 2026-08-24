using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesOrders.Queries.GetVanSalesCustomerOrderByClientRequestId;

/// <summary>
/// Did my idempotency key already produce an order?
/// </summary>
/// <remarks>
/// The one question a handset must be able to ask after a submit whose reply it never saw. Without
/// it the app has two bad options: send again and risk a second delivery, or drop the order and
/// leave the shopkeeper waiting for stock nobody is bringing.
/// </remarks>
public sealed record GetVanSalesCustomerOrderByClientRequestIdQuery(
    int AccountId,
    string? ClientRequestId
) : IRequest<ErrorOr<VanSalesOrderResult>>;
