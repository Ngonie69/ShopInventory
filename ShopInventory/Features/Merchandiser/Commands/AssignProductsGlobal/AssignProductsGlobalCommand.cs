using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Merchandiser.Commands.AssignProductsGlobal;

public sealed record AssignProductsGlobalCommand(
    AssignMerchandiserProductsRequest Request,
    string Username
) : IRequest<ErrorOr<MerchandiserProductListResponseDto>>;
