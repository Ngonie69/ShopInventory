using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Services;

namespace ShopInventory.Features.Merchandiser.Queries.GetProductCategories;

public sealed class GetProductCategoriesHandler(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    ILogger<GetProductCategoriesHandler> logger
) : IRequestHandler<GetProductCategoriesQuery, ErrorOr<List<string>>>
{
    public async Task<ErrorOr<List<string>>> Handle(
        GetProductCategoriesQuery request,
        CancellationToken cancellationToken)
    {
        var activeItemCodes = await context.MerchandiserProducts
            .AsNoTracking()
            .Where(mp => mp.MerchandiserUserId == request.UserId && mp.IsActive)
            .Select(mp => mp.ItemCode)
            .ToListAsync(cancellationToken);

        if (activeItemCodes.Count == 0)
            return new List<string>();

        try
        {
            var inClause = string.Join(",", activeItemCodes.Select(c => $"'{c.Replace("'", "''")}'"));
            var sqlText = $@"
                SELECT DISTINCT T0.""U_ItemGroup""
                FROM OITM T0
                WHERE T0.""ItemCode"" IN ({inClause})
                  AND T0.""U_ItemGroup"" IS NOT NULL
                  AND T0.""U_ItemGroup"" <> ''
                ORDER BY T0.""U_ItemGroup""";

            var rows = await sapClient.ExecuteRawSqlQueryAsync(
                "MerchCategories", "Merchandiser Product Categories", sqlText, cancellationToken);

            var categories = rows
                .Select(r => r.GetValueOrDefault("U_ItemGroup")?.ToString())
                .Where(c => !string.IsNullOrEmpty(c))
                .Cast<string>()
                .ToList();

            return categories;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch product categories from SAP");
            return new List<string>();
        }
    }
}
