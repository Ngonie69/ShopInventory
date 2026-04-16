using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Merchandiser.Queries.GetGlobalProducts;

public sealed record GetGlobalProductsQuery() : IRequest<ErrorOr<MerchandiserProductListResponseDto>>;
