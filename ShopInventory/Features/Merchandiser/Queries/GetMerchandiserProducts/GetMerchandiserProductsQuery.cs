using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Merchandiser.Queries.GetMerchandiserProducts;

public sealed record GetMerchandiserProductsQuery(
    Guid UserId
) : IRequest<ErrorOr<MerchandiserProductListResponseDto>>;
