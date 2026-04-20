using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Merchandiser.Queries.GetActiveProducts;

public sealed record GetActiveProductsQuery(
    Guid UserId,
    string? Search,
    string? Category,
    int Page = 1,
    int PageSize = 0
) : IRequest<ErrorOr<MerchandiserActiveProductListResponseDto>>;
