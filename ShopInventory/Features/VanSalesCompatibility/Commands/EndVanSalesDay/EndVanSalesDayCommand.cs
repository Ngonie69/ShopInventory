using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.VanSalesCompatibility.Commands.EndVanSalesDay;

public sealed record EndVanSalesDayCommand(
    VanSalesEndDayRequest Request,
    Guid UserId
) : IRequest<ErrorOr<VanSalesRouteDayResponse>>;
