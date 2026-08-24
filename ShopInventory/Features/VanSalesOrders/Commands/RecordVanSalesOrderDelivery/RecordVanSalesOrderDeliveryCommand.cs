using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesOrders.Commands.RecordVanSalesOrderDelivery;

/// <summary>How much of one line actually changed hands.</summary>
public sealed record RecordVanSalesDeliveryLine(int LineNumber, decimal QuantityFulfilled);

/// <summary>
/// Record what was actually delivered against an order.
/// </summary>
/// <remarks>
/// The step that closes the loop the WhatsApp channel never had. An order said what was wanted;
/// this says what arrived, and the gap between them is the number both sides can now read instead
/// of remember.
/// </remarks>
public sealed record RecordVanSalesOrderDeliveryCommand(
    int OrderId,
    IReadOnlyList<RecordVanSalesDeliveryLine> Lines,
    Guid? RecordedByUserId
) : IRequest<ErrorOr<VanSalesOrderResult>>;
