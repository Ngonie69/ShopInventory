using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Products.Queries.GetAllProducts;

public sealed record GetAllProductsQuery() : IRequest<ErrorOr<ProductsListResponseDto>>;
