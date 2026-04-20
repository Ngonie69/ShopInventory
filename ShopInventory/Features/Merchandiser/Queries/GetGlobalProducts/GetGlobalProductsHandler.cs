using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Merchandiser.Queries.GetGlobalProducts;

public sealed class GetGlobalProductsHandler(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    ILogger<GetGlobalProductsHandler> logger
) : IRequestHandler<GetGlobalProductsQuery, ErrorOr<MerchandiserProductListResponseDto>>
{
    public async Task<ErrorOr<MerchandiserProductListResponseDto>> Handle(
        GetGlobalProductsQuery request,
        CancellationToken cancellationToken)
    {
        // Backfill any missing item names or categories from SAP
        var missingData = await context.MerchandiserProducts
            .Where(mp => (mp.ItemName == null || mp.ItemName == "") || (mp.Category == null || mp.Category == ""))
            .Select(mp => mp.ItemCode)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (missingData.Count > 0)
        {
            try
            {
                var inClause = string.Join(",", missingData.Select(c => $"'{c.Replace("'", "''")}'"));
                var sqlText = $@"SELECT T0.""ItemCode"", T0.""ItemName"", T0.""U_ItemGroup"" AS ""Category"", T0.""SalUnitMsr"" AS ""UoM"" FROM OITM T0 WHERE T0.""ItemCode"" IN ({inClause}) ORDER BY T0.""ItemCode""";
                var rows = await sapClient.ExecuteRawSqlQueryAsync("MerchBackfill", "Backfill Item Names/Categories", sqlText, cancellationToken);
                var detailMap = rows
                    .Where(r => r.GetValueOrDefault("ItemCode") != null)
                    .ToDictionary(
                        r => r["ItemCode"]!.ToString()!,
                        r => (
                            ItemName: r.GetValueOrDefault("ItemName")?.ToString() ?? "",
                            Category: (r.GetValueOrDefault("Category") ?? r.GetValueOrDefault("U_ItemGroup"))?.ToString(),
                            UoM: (r.GetValueOrDefault("UoM") ?? r.GetValueOrDefault("SalUnitMsr"))?.ToString()
                        ),
                        StringComparer.OrdinalIgnoreCase);

                var toUpdate = await context.MerchandiserProducts
                    .Where(mp => (mp.ItemName == null || mp.ItemName == "") || (mp.Category == null || mp.Category == ""))
                    .ToListAsync(cancellationToken);

                foreach (var mp in toUpdate)
                {
                    if (detailMap.TryGetValue(mp.ItemCode, out var detail))
                    {
                        if (string.IsNullOrEmpty(mp.ItemName))
                            mp.ItemName = detail.ItemName;
                        if (string.IsNullOrEmpty(mp.Category))
                            mp.Category = detail.Category;
                        if (string.IsNullOrEmpty(mp.UoM))
                            mp.UoM = detail.UoM;
                    }
                }
                await context.SaveChangesAsync(cancellationToken);
                logger.LogInformation("Backfilled {Count} merchandiser product records from SAP", toUpdate.Count);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to backfill item details from SAP");
            }
        }

        // Get distinct products across all merchandisers (they are uniform)
        var allProducts = await context.MerchandiserProducts
            .AsNoTracking()
            .OrderBy(mp => mp.ItemCode)
            .ToListAsync(cancellationToken);

        var products = allProducts
            .GroupBy(mp => mp.ItemCode)
            .Select(g =>
            {
                var representative = g.Where(mp => mp.UpdatedAt != null)
                    .OrderByDescending(mp => mp.UpdatedAt)
                    .FirstOrDefault() ?? g.OrderByDescending(mp => mp.CreatedAt).First();
                return representative;
            })
            .OrderBy(mp => mp.ItemCode)
            .Select(mp => new MerchandiserProductDto
            {
                Id = mp.Id,
                ItemCode = mp.ItemCode,
                ItemName = mp.ItemName,
                Category = mp.Category,
                IsActive = mp.IsActive,
                CreatedAt = mp.CreatedAt,
                UpdatedAt = mp.UpdatedAt,
                UpdatedBy = mp.UpdatedBy
            })
            .ToList();

        return new MerchandiserProductListResponseDto
        {
            MerchandiserName = "All Merchandisers",
            TotalCount = products.Count,
            ActiveCount = products.Count(p => p.IsActive),
            Products = products
        };
    }
}
