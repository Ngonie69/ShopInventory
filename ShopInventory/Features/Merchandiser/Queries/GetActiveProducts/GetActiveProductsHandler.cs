using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Merchandiser.Queries.GetActiveProducts;

public sealed class GetActiveProductsHandler(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    IAuditService auditService,
    ILogger<GetActiveProductsHandler> logger
) : IRequestHandler<GetActiveProductsQuery, ErrorOr<MerchandiserActiveProductListResponseDto>>
{
    private readonly ILogger<GetActiveProductsHandler> _logger = logger;

    public async Task<ErrorOr<MerchandiserActiveProductListResponseDto>> Handle(
        GetActiveProductsQuery request,
        CancellationToken cancellationToken)
    {
        var userExists = await context.Users.AsNoTracking()
            .AnyAsync(u => u.Id == request.UserId, cancellationToken);

        if (!userExists)
            return Errors.Merchandiser.Unauthenticated;

        // Backfill missing Category/UoM from SAP (one-time self-healing for existing records)
        await BackfillMissingFieldsAsync(cancellationToken);

        // All merchandisers see all active products — load and deduplicate by ItemCode
        var allActive = await context.MerchandiserProducts
            .AsNoTracking()
            .Where(mp => mp.IsActive)
            .ToListAsync(cancellationToken);

        var distinct = allActive
            .GroupBy(mp => mp.ItemCode, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .AsEnumerable();

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search;
            distinct = distinct.Where(mp =>
                mp.ItemCode.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (mp.ItemName != null && mp.ItemName.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
                (mp.BarCode != null && mp.BarCode.Contains(search, StringComparison.OrdinalIgnoreCase)));
        }

        if (!string.IsNullOrWhiteSpace(request.Category))
        {
            distinct = distinct.Where(mp => string.Equals(mp.Category, request.Category, StringComparison.OrdinalIgnoreCase));
        }

        var filtered = distinct.OrderBy(mp => mp.ItemName ?? mp.ItemCode).ToList();
        var totalCount = filtered.Count;

        int page, pageSize;
        IEnumerable<MerchandiserActiveProductDto> projected;

        if (request.PageSize <= 0)
        {
            page = 1;
            pageSize = totalCount;
            projected = filtered.Select(mp => new MerchandiserActiveProductDto
            {
                ItemCode = mp.ItemCode,
                ItemName = mp.ItemName,
                BarCode = mp.BarCode,
                Price = 0,
                UoM = mp.UoM,
                Category = mp.Category
            });
        }
        else
        {
            page = request.Page;
            pageSize = request.PageSize;
            projected = filtered
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(mp => new MerchandiserActiveProductDto
                {
                    ItemCode = mp.ItemCode,
                    ItemName = mp.ItemName,
                    BarCode = mp.BarCode,
                    Price = 0,
                    UoM = mp.UoM,
                    Category = mp.Category
                });
        }

        var products = projected.ToList();

        var response = new MerchandiserActiveProductListResponseDto
        {
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            Products = products
        };

        try
        {
            var searchLabel = string.IsNullOrWhiteSpace(request.Search) ? "none" : request.Search.Trim();
            var categoryLabel = string.IsNullOrWhiteSpace(request.Category) ? "all" : request.Category.Trim();
            await auditService.LogAsync(
                AuditActions.ViewMobileProducts,
                "MerchandiserProduct",
                null,
                $"Viewed mobile products (search: {searchLabel}, category: {categoryLabel}, page: {page}, size: {pageSize}). Returned {products.Count} of {totalCount} products.",
                true);
        }
        catch
        {
        }

        return response;
    }

    private async Task BackfillMissingFieldsAsync(CancellationToken cancellationToken)
    {
        var missingCodes = await context.MerchandiserProducts
            .Where(mp => mp.IsActive
                && (mp.Category == null || mp.Category == ""))
            .Select(mp => mp.ItemCode)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (missingCodes.Count == 0)
            return;

        try
        {
            var inClause = string.Join(",", missingCodes.Select(c => $"'{c.Replace("'", "''")}'"));
            var sqlText = $@"
                SELECT T0.""ItemCode"", T0.""U_ItemGroup"" AS ""Category"", T0.""SalUnitMsr"" AS ""UoM""
                FROM OITM T0
                WHERE T0.""ItemCode"" IN ({inClause})";

            var rows = await sapClient.ExecuteRawSqlQueryAsync(
                "MerchActiveBackfill", "Backfill Category/UoM", sqlText, cancellationToken);

            var detailMap = rows
                .Where(r => r.GetValueOrDefault("ItemCode") != null)
                .ToDictionary(
                    r => r["ItemCode"]!.ToString()!,
                    r => (
                        Category: (r.GetValueOrDefault("Category") ?? r.GetValueOrDefault("U_ItemGroup"))?.ToString(),
                        UoM: (r.GetValueOrDefault("UoM") ?? r.GetValueOrDefault("SalUnitMsr"))?.ToString()
                    ),
                    StringComparer.OrdinalIgnoreCase);

            // Update ALL merchandiser records for these item codes (not just this user)
            var toUpdate = await context.MerchandiserProducts
                .Where(mp => missingCodes.Contains(mp.ItemCode)
                    && (mp.Category == null || mp.Category == ""))
                .ToListAsync(cancellationToken);

            foreach (var mp in toUpdate)
            {
                if (detailMap.TryGetValue(mp.ItemCode, out var detail))
                {
                    mp.Category = detail.Category;
                    if (string.IsNullOrEmpty(mp.UoM))
                        mp.UoM = detail.UoM;
                }
            }

            await context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Backfilled Category/UoM for {Count} merchandiser product records from SAP", toUpdate.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to backfill Category/UoM from SAP for merchandiser products");
        }
    }
}
