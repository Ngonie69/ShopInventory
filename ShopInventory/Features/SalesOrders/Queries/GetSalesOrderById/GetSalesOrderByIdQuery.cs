using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.SalesOrders.Queries.GetSalesOrderById;

public sealed record GetSalesOrderByIdQuery(int Id) : IRequest<ErrorOr<SalesOrderDto>>;
