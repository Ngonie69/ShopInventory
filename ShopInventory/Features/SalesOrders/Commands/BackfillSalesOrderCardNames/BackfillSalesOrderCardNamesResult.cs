namespace ShopInventory.Features.SalesOrders.Commands.BackfillSalesOrderCardNames;

public sealed record BackfillSalesOrderCardNamesResult(
    int OrdersUpdated,
    int CustomersResolved,
    int CustomersUnresolved);