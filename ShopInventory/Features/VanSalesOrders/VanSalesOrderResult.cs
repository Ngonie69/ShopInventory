using ShopInventory.Models.Entities;

namespace ShopInventory.Features.VanSalesOrders;

/// <summary>One line of an order, as the customer sees it back.</summary>
/// <remarks>
/// <c>QuantityFulfilled</c> is sent from the moment the order exists, sitting at zero until a
/// delivery is recorded. The app shows ordered against delivered on the order screen, which is what
/// turns "they short-delivered us again" from an argument into a figure.
/// </remarks>
public sealed record VanSalesOrderLineResult(
    int LineNumber,
    string ItemCode,
    string? ItemDescription,
    string? UnitOfMeasure,
    decimal QuantityOrdered,
    decimal QuantityFulfilled,
    decimal UnitPrice,
    decimal TaxPercent,
    decimal LineTotal);

/// <summary>
/// A customer's order as the app displays it.
/// </summary>
/// <remarks>
/// <c>ClientRequestId</c> is echoed back deliberately. It is how a handset that never saw a reply
/// matches the order it finds on the server to the draft still sitting in its outbox, and without
/// it the app cannot tell "this went through" from "this is somebody else's order".
/// </remarks>
public sealed record VanSalesOrderResult(
    int Id,
    string OrderNumber,
    string ClientRequestId,
    string CustomerCode,
    string CustomerName,
    string? RouteCode,
    DateTime? RequestedVisitDate,
    VanSalesOrderStatus Status,
    string? Currency,
    decimal SubTotal,
    decimal TaxAmount,
    decimal DocTotal,
    string? CustomerNotes,
    DateTime ReceivedAtUtc,
    DateTime? CancelledAtUtc,
    DateTime? DeliveredAtUtc,
    IReadOnlyList<VanSalesOrderLineResult> Lines);
