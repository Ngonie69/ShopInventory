using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesSalesOrderHistory;

public sealed record GetVanSalesSalesOrderHistoryQuery(
    Guid UserId,
    VanSalesOrderSearchRequest Request) : IRequest<ErrorOr<List<VanSalesLegacyOrderDto>>>;