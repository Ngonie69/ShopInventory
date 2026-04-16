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
        // Backfill any missing item names from SAP
        var missingNames = await context.MerchandiserProducts
            .Where(mp => mp.ItemName == null || mp.ItemName == "")
            .Select(mp => mp.ItemCode)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (missingNames.Count > 0)
        {
            try
            {
                var inClause = string.Join(",", missingNames.Select(c => $"'{c.Replace("'", "''")}'"));
                var sqlText = $"SELECT T0.\"ItemCode\", T0.\"ItemName\" FROM OITM T0 WHERE T0.\"ItemCode\" IN ({inClause}) ORDER BY T0.\"ItemCode\"";
                var rows = await sapClient.ExecuteRawSqlQueryAsync("MerchBackfill", "Backfill Item Names", sqlText, cancellationToken);
                var nameMap = rows
                    .Where(r => r.GetValueOrDefault("ItemCode") != null)
                    .ToDictionary(
                        r => r["ItemCode"]!.ToString()!,
                        r => r.GetValueOrDefault("ItemName")?.ToString() ?? "",
                        StringComparer.OrdinalIgnoreCase);

                var toUpdate = await context.MerchandiserProducts
                    .Where(mp => mp.ItemName == null || mp.ItemName == "")
                    .ToListAsync(cancellationToken);

                foreach (var mp in toUpdate)
                {
                    if (nameMap.TryGetValue(mp.ItemCode, out var name))
                    {
                        mp.ItemName = name;
                    }
                }
                await context.SaveChangesAsync(cancellationToken);
                logger.LogInformation("Backfilled {Count} merchandiser product names from SAP", toUpdate.Count);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to backfill item names from SAP");
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
