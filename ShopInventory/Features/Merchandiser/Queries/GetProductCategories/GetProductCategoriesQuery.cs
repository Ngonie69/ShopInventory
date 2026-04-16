using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Merchandiser.Queries.GetProductCategories;

public sealed record GetProductCategoriesQuery(
    Guid UserId
) : IRequest<ErrorOr<List<string>>>;
