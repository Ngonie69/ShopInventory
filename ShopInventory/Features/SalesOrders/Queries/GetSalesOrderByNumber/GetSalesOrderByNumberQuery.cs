using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.SalesOrders.Queries.GetSalesOrderByNumber;

public sealed record GetSalesOrderByNumberQuery(string OrderNumber) : IRequest<ErrorOr<SalesOrderDto>>;
