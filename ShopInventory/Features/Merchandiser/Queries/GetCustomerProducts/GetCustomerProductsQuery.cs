using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Merchandiser.Queries.GetCustomerProducts;

public sealed record GetCustomerProductsQuery(
    Guid UserId,
    string CardCode,
    string? Search,
    string? Category
) : IRequest<ErrorOr<List<MerchandiserActiveProductDto>>>;
