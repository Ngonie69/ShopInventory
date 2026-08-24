namespace ShopInventory.Features.VanSalesOrders.Commands.ConvertVanSalesOrderToSalesOrder;

/// <summary>Both halves of the crossing: the intake order and the document it became.</summary>
public sealed record VanSalesOrderConversionResult(
    int VanSalesOrderId,
    string VanSalesOrderNumber,
    int SalesOrderId,
    string SalesOrderNumber);
