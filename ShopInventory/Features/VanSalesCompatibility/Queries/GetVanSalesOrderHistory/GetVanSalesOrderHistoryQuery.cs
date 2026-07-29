using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesOrderHistory;

public sealed record GetVanSalesOrderHistoryQuery(
    Guid UserId,
    VanSalesOrderSearchRequest Request) : IRequest<ErrorOr<List<VanSalesLegacyOrderDto>>>;