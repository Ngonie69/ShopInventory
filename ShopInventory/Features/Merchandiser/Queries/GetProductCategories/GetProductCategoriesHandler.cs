using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;

namespace ShopInventory.Features.Merchandiser.Queries.GetProductCategories;

public sealed class GetProductCategoriesHandler(
    ApplicationDbContext context
) : IRequestHandler<GetProductCategoriesQuery, ErrorOr<List<string>>>
{
    public async Task<ErrorOr<List<string>>> Handle(
        GetProductCategoriesQuery request,
        CancellationToken cancellationToken)
    {
        // All merchandisers see same categories (no per-user filtering)
        var categories = await context.MerchandiserProducts
            .AsNoTracking()
            .Where(mp => mp.IsActive && mp.Category != null && mp.Category != "")
            .Select(mp => mp.Category!)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync(cancellationToken);

        return categories;
    }
}
