using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Merchandiser.Commands.AssignProducts;

public sealed record AssignProductsCommand(
    Guid UserId,
    AssignMerchandiserProductsRequest Request,
    string Username
) : IRequest<ErrorOr<MerchandiserProductListResponseDto>>;
