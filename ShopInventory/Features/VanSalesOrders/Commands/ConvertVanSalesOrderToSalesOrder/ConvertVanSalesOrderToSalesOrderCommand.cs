using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesOrders.Commands.ConvertVanSalesOrderToSalesOrder;

/// <summary>
/// Turn a customer's order into a sales order in the ERP pipeline.
/// </summary>
/// <remarks>
/// Always a staff act, never the customer's. This is the one crossing between the standalone intake
/// and the tables that feed SAP posting, and the reason the intake is standalone at all is that the
/// crossing should be deliberate and attributable.
/// </remarks>
public sealed record ConvertVanSalesOrderToSalesOrderCommand(
    int OrderId,
    Guid UserId
) : IRequest<ErrorOr<VanSalesOrderConversionResult>>;
