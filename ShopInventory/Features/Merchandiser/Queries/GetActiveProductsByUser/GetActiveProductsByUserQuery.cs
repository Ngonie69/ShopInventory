using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Merchandiser.Queries.GetActiveProductsByUser;

public sealed record GetActiveProductsByUserQuery(
    Guid UserId,
    string? Search,
    string? Category
) : IRequest<ErrorOr<List<MerchandiserActiveProductDto>>>;
