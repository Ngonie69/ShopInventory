using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.SalesOrders.Queries.GetLocalSalesOrderById;

public sealed record GetLocalSalesOrderByIdQuery(int Id) : IRequest<ErrorOr<SalesOrderDto>>;
