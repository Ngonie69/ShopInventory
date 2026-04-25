using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Merchandiser.Queries.GetProductCategories;

public sealed class GetProductCategoriesHandler(
    ApplicationDbContext context,
    IAuditService auditService
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

        try
        {
            await auditService.LogAsync(
                AuditActions.ViewMobileCategories,
                "MerchandiserProduct",
                null,
                $"Viewed mobile product categories. Returned {categories.Count} categories.",
                true);
        }
        catch
        {
        }

        return categories;
    }
}
