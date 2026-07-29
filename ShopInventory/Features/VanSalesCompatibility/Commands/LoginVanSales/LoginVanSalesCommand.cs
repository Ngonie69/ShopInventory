using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.VanSalesCompatibility.Commands.LoginVanSales;

public sealed record LoginVanSalesCommand(
    AuthLoginRequest Request,
    string IpAddress
) : IRequest<ErrorOr<VanSalesLoginResponse>>;