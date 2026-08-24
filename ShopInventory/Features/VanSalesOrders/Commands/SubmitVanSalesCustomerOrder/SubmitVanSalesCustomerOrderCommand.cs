using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesOrders.Commands.SubmitVanSalesCustomerOrder;

/// <summary>One item on a submitted order, as the handset sends it.</summary>
/// <remarks>
/// No price. The handset shows what it cached, but what the customer is charged is decided here
/// against the current price list — otherwise a stale catalogue, or an edited request, sets the
/// price.
/// </remarks>
public sealed record SubmitVanSalesCustomerOrderLine(
    string? ItemCode,
    decimal Quantity);

/// <summary>
/// A van sales customer sending their own order.
/// </summary>
/// <remarks>
/// <c>ClientRequestId</c> is minted by the app when the draft is created, not when it is sent, and
/// is what makes retrying safe. <c>SubmittedAtUtc</c> is the handset's clock and is recorded but
/// never trusted; the server decides everything on its own time.
/// </remarks>
public sealed record SubmitVanSalesCustomerOrderCommand(
    int AccountId,
    string? ClientRequestId,
    IReadOnlyList<SubmitVanSalesCustomerOrderLine> Lines,
    DateTime? RequestedVisitDate,
    string? CustomerNotes,
    DateTime? SubmittedAtUtc,
    string? DeviceInfo,
    string? AppVersion,
    double? Latitude,
    double? Longitude
) : IRequest<ErrorOr<VanSalesOrderResult>>;
