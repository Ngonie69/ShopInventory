using ErrorOr;
using MediatR;

namespace ShopInventory.Features.SalesOrders.Commands.DeleteSalesOrder;

public sealed record DeleteSalesOrderCommand(int Id) : IRequest<ErrorOr<Deleted>>;
