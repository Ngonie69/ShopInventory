using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Merchandiser.Queries.GetGlobalProducts;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.Merchandiser.Commands.AssignProductsGlobal;

public sealed class AssignProductsGlobalHandler(
    ApplicationDbContext context,
    ISender sender,
    ISAPServiceLayerClient sapClient,
    ILogger<AssignProductsGlobalHandler> logger
) : IRequestHandler<AssignProductsGlobalCommand, ErrorOr<MerchandiserProductListResponseDto>>
{
    public async Task<ErrorOr<MerchandiserProductListResponseDto>> Handle(
        AssignProductsGlobalCommand command,
        CancellationToken cancellationToken)
    {
        var request = command.Request;

        if (request.ItemCodes == null || request.ItemCodes.Count == 0)
            return Errors.Merchandiser.AssignmentFailed("At least one item code is required");

        var merchandiserIds = await context.Users
            .AsNoTracking()
            .Where(u => u.Role == "Merchandiser" && u.IsActive)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);

        // Fetch product details from SAP for denormalized fields
        var productDetails = await GetProductDetailsAsync(request.ItemCodes, cancellationToken);

        // Override with request-provided names if available
        if (request.ItemNames is { Count: > 0 })
        {
            foreach (var (code, name) in request.ItemNames)
            {
                if (productDetails.TryGetValue(code, out var detail))
                    productDetails[code] = detail with { ItemName = name };
                else
                    productDetails[code] = new ProductDetailInfo { ItemName = name };
            }
        }

        var globalStatus = await context.MerchandiserProducts
            .AsNoTracking()
            .Where(mp => request.ItemCodes.Contains(mp.ItemCode) && mp.UpdatedAt != null)
            .GroupBy(mp => mp.ItemCode)
            .Select(g => new { ItemCode = g.Key, IsActive = g.OrderByDescending(mp => mp.UpdatedAt).First().IsActive })
            .ToDictionaryAsync(x => x.ItemCode, x => x.IsActive, cancellationToken);

        int totalAdded = 0;

        foreach (var merchandiserId in merchandiserIds)
        {
            var existing = await context.MerchandiserProducts
                .Where(mp => mp.MerchandiserUserId == merchandiserId)
                .Select(mp => mp.ItemCode)
                .ToListAsync(cancellationToken);

            var newCodes = request.ItemCodes.Except(existing).ToList();

            var newEntities = newCodes.Select(code =>
            {
                var details = productDetails.GetValueOrDefault(code);
                return new MerchandiserProductEntity
                {
                    MerchandiserUserId = merchandiserId,
                    ItemCode = code,
                    ItemName = details?.ItemName,
                    BarCode = details?.BarCode,
                    UoM = details?.UoM,
                    Category = details?.Category,
                    IsActive = globalStatus.GetValueOrDefault(code, true),
                    CreatedAt = DateTime.UtcNow,
                    UpdatedBy = command.Username
                };
            }).ToList();

            context.MerchandiserProducts.AddRange(newEntities);
            totalAdded += newEntities.Count;
        }

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Assigned {Count} products to {MerchCount} merchandisers",
            request.ItemCodes.Count, merchandiserIds.Count);

        return await sender.Send(new GetGlobalProductsQuery(), cancellationToken);
    }

    private async Task<Dictionary<string, ProductDetailInfo>> GetProductDetailsAsync(
        List<string> itemCodes, CancellationToken cancellationToken)
    {
        if (itemCodes.Count == 0)
            return new();

        try
        {
            var inClause = string.Join(",", itemCodes.Select(c => $"'{c.Replace("'", "''")}'"));
            var sqlText = $@"
                SELECT T0.""ItemCode"", T0.""ItemName"", T0.""U_ItemGroup"" AS ""Category"",
                       T0.""CodeBars"" AS ""BarCode"", T0.""SalUnitMsr"" AS ""UoM""
                FROM OITM T0
                WHERE T0.""ItemCode"" IN ({inClause})";

            var rows = await sapClient.ExecuteRawSqlQueryAsync(
                "MerchAssignGlobalDetails", "Merchandiser Global Assignment Details", sqlText, cancellationToken);

            return rows.ToDictionary(
                r => r.GetValueOrDefault("ItemCode")?.ToString() ?? "",
                r => new ProductDetailInfo
                {
                    ItemName = r.GetValueOrDefault("ItemName")?.ToString(),
                    BarCode = (r.GetValueOrDefault("BarCode") ?? r.GetValueOrDefault("CodeBars"))?.ToString(),
                    UoM = (r.GetValueOrDefault("UoM") ?? r.GetValueOrDefault("SalUnitMsr"))?.ToString(),
                    Category = (r.GetValueOrDefault("Category") ?? r.GetValueOrDefault("U_ItemGroup"))?.ToString()
                });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch product details from SAP during global assignment");
        }

        // Fallback to local Products table
        var localProducts = await context.Products
            .AsNoTracking()
            .Where(p => itemCodes.Contains(p.ItemCode))
            .Select(p => new { p.ItemCode, p.ItemName, p.BarCode, UoM = p.SalesUnit ?? p.InventoryUOM })
            .ToDictionaryAsync(p => p.ItemCode, cancellationToken);

        return localProducts.ToDictionary(
            kvp => kvp.Key,
            kvp => new ProductDetailInfo
            {
                ItemName = kvp.Value.ItemName,
                BarCode = kvp.Value.BarCode,
                UoM = kvp.Value.UoM,
                Category = null
            });
    }

    private sealed record ProductDetailInfo
    {
        public string? ItemName { get; init; }
        public string? BarCode { get; init; }
        public string? UoM { get; init; }
        public string? Category { get; init; }
    }
}
