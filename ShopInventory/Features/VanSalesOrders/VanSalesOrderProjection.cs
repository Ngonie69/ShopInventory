using System.Linq.Expressions;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.VanSalesOrders;

/// <summary>
/// The one way a van sales order becomes a <see cref="VanSalesOrderResult"/>.
/// </summary>
/// <remarks>
/// An expression rather than a method so EF composes it into the SQL and reads only the columns the
/// result needs — the repo's rule that queries project rather than materialise entities.
/// <para>
/// Shared by every read of an order. Three endpoints return one of these — the list, the single
/// order, and the idempotency lookup — and a handset comparing what it got back from a submit with
/// what it later fetched has to see the same shape both times, or reconciling an offline order
/// becomes guesswork.
/// </para>
/// </remarks>
public static class VanSalesOrderProjection
{
    public static readonly Expression<Func<VanSalesOrderEntity, VanSalesOrderResult>> ToResult =
        order => new VanSalesOrderResult(
            order.Id,
            order.OrderNumber,
            order.ClientRequestId,
            order.RouteCustomerCode,
            order.RouteCustomerName,
            order.RouteCode,
            order.RequestedVisitDate,
            order.Status,
            order.Currency,
            order.SubTotal,
            order.TaxAmount,
            order.DocTotal,
            order.CustomerNotes,
            order.ReceivedAtUtc,
            order.CancelledAtUtc,
            order.DeliveredAtUtc,
            order.Lines
                .OrderBy(line => line.LineNumber)
                .Select(line => new VanSalesOrderLineResult(
                    line.LineNumber,
                    line.ItemCode,
                    line.ItemDescription,
                    line.UoMCode,
                    line.QuantityOrdered,
                    line.QuantityFulfilled,
                    line.UnitPrice,
                    line.TaxPercent,
                    line.LineTotal))
                .ToList());

    /// <summary>
    /// The same mapping for an entity already in memory, for the handler that has just created one.
    /// </summary>
    /// <remarks>
    /// Compiled from the expression above rather than written out a second time. Two hand-written
    /// mappings would drift, and the drift would show up as a submit response that disagrees with
    /// the order the app fetches a minute later — precisely the comparison the offline
    /// reconciliation depends on.
    /// </remarks>
    public static readonly Func<VanSalesOrderEntity, VanSalesOrderResult> ToResultInMemory =
        ToResult.Compile();
}
