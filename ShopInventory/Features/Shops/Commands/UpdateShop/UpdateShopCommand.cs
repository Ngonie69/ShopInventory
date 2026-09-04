using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Shops.Commands.UpdateShop;

/// <summary>
/// Changes a shop's details. <c>UserId</c> is the administrator doing it, recorded on the row.
/// </summary>
public sealed record UpdateShopCommand(
    int ShopId,
    UpdateShopRequest Request,
    Guid UserId
) : IRequest<ErrorOr<ShopDto>>;
