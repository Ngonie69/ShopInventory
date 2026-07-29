using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.VanSalesCompatibility.Commands.RefreshVanSales;

public sealed record RefreshVanSalesCommand(
    RefreshTokenRequest Request,
    string IpAddress
) : IRequest<ErrorOr<VanSalesLoginResponse>>;