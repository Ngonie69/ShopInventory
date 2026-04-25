using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Merchandiser.Queries.GetCustomerProducts;

public sealed class GetCustomerProductsHandler(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    ILogger<GetCustomerProductsHandler> logger
) : IRequestHandler<GetCustomerProductsQuery, ErrorOr<List<MerchandiserActiveProductDto>>>
{
    public async Task<ErrorOr<List<MerchandiserActiveProductDto>>> Handle(
        GetCustomerProductsQuery request,
        CancellationToken cancellationToken)
    {
        var user = await context.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == request.UserId, cancellationToken);

        if (user == null)
            return Errors.Merchandiser.Unauthenticated;

        // All merchandisers share the same active product list (no per-user filtering)
        var activeItemCodes = await context.MerchandiserProducts
            .AsNoTracking()
            .Where(mp => mp.IsActive)
            .Select(mp => mp.ItemCode)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (activeItemCodes.Count == 0)
            return new List<MerchandiserActiveProductDto>();

        return await GetProductDetailsFromSAP(activeItemCodes, request.CardCode, request.Search, request.Category, cancellationToken);
    }

    private async Task<List<MerchandiserActiveProductDto>> GetProductDetailsFromSAP(
        List<string> itemCodes,
        string? cardCode = null,
        string? search = null,
        string? category = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var inClause = string.Join(",", itemCodes.Select(c => $"'{c.Replace("'", "''")}'"));

            var priceListJoin = "LEFT JOIN ITM1 T1 ON T0.\"ItemCode\" = T1.\"ItemCode\" AND T1.\"PriceList\" = 1";
            if (!string.IsNullOrEmpty(cardCode))
            {
                var safeCardCode = cardCode.Replace("'", "''");
                var customerPriceListPredicate = SapSqlPriceListExpressions.BuildFallbackPredicate(@"T1.""PriceList""", @"T2.""ListNum""");
                priceListJoin = $@"LEFT JOIN OCRD T2 ON T2.""CardCode"" = '{safeCardCode}'
                LEFT JOIN ITM1 T1 ON T0.""ItemCode"" = T1.""ItemCode"" AND {customerPriceListPredicate}";
            }

            var categoryFilter = "";
            if (!string.IsNullOrWhiteSpace(category))
            {
                var safeCategory = category.Replace("'", "''");
                categoryFilter = $@" AND T0.""U_ItemGroup"" = '{safeCategory}'";
            }

            var sqlText = $@"
                SELECT T0.""ItemCode"", T0.""ItemName"", T0.""CodeBars"" AS ""BarCode"",
                       T0.""SalUnitMsr"" AS ""UoM"",
                       T0.""InvntryUom"" AS ""InventoryUOM"",
                       T0.""U_ItemGroup"" AS ""Category"",
                       T1.""Price""
                FROM OITM T0
                {priceListJoin}
                WHERE T0.""ItemCode"" IN ({inClause}){categoryFilter}
                ORDER BY T0.""ItemName""";

            var rows = await sapClient.ExecuteRawSqlQueryAsync(
                "MerchActiveProducts", "Merchandiser Active Products", sqlText, cancellationToken);

            var filteredRows = FilterRowsBySearch(rows, search);

            return filteredRows.Select(r => new MerchandiserActiveProductDto
            {
                ItemCode = r.GetValueOrDefault("ItemCode")?.ToString() ?? "",
                ItemName = r.GetValueOrDefault("ItemName")?.ToString(),
                BarCode = (r.GetValueOrDefault("BarCode") ?? r.GetValueOrDefault("CodeBars"))?.ToString(),
                UoM = (r.GetValueOrDefault("UoM") ?? r.GetValueOrDefault("SalUnitMsr"))?.ToString() ?? r.GetValueOrDefault("InventoryUOM")?.ToString(),
                Price = decimal.TryParse(r.GetValueOrDefault("Price")?.ToString(), out var price) ? price : 0,
                Category = (r.GetValueOrDefault("Category") ?? r.GetValueOrDefault("U_ItemGroup"))?.ToString()
            }).ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch merchandiser products from SAP, falling back to local DB");

            var query = context.MerchandiserProducts
                .AsNoTracking()
                .Where(mp => itemCodes.Contains(mp.ItemCode) && mp.IsActive);

            if (!string.IsNullOrWhiteSpace(search))
            {
                var pattern = $"%{search.Trim()}%";
                query = query.Where(mp =>
                    EF.Functions.ILike(mp.ItemCode, pattern) ||
                    (mp.ItemName != null && EF.Functions.ILike(mp.ItemName, pattern)));
            }

            return await query
                .OrderBy(mp => mp.ItemName ?? mp.ItemCode)
                .Select(mp => new MerchandiserActiveProductDto
                {
                    ItemCode = mp.ItemCode,
                    ItemName = mp.ItemName
                })
                .ToListAsync(cancellationToken);
        }
    }

    private static List<Dictionary<string, object?>> FilterRowsBySearch(
        List<Dictionary<string, object?>> rows,
        string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return rows;

        var term = search.Trim();
        return rows
            .Where(row =>
                ContainsIgnoreCase(row.GetValueOrDefault("ItemCode")?.ToString(), term) ||
                ContainsIgnoreCase(row.GetValueOrDefault("ItemName")?.ToString(), term) ||
                ContainsIgnoreCase((row.GetValueOrDefault("BarCode") ?? row.GetValueOrDefault("CodeBars"))?.ToString(), term))
            .ToList();
    }

    private static bool ContainsIgnoreCase(string? value, string term) =>
        value?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;
}
