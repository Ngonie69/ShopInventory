using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.VanSalesCompatibility.Commands.StartVanSalesDay;

public sealed record StartVanSalesDayCommand(
    VanSalesStartDayRequest Request,
    Guid UserId
) : IRequest<ErrorOr<VanSalesRouteDayResponse>>;
