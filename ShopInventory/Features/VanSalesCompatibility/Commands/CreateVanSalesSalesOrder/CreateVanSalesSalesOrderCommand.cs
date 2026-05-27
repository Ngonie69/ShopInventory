using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.VanSalesCompatibility.Commands.CreateVanSalesSalesOrder;

public sealed record CreateVanSalesSalesOrderCommand(
    VanSalesOrderRequest Request,
    Guid UserId) : IRequest<ErrorOr<VanSalesLegacyOrderDto>>;