using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Shops.Commands.CreateShop;

/// <summary>
/// Opens a shop. <c>UserId</c> is the administrator doing it, recorded on the row.
/// </summary>
public sealed record CreateShopCommand(
    CreateShopRequest Request,
    Guid UserId
) : IRequest<ErrorOr<ShopDto>>;
