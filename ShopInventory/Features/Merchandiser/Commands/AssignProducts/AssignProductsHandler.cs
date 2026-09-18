using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Merchandiser.Queries.GetMerchandiserProducts;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.Merchandiser.Commands.AssignProducts;

public sealed class AssignProductsHandler(
    ApplicationDbContext context,
    ISender sender,
    ISAPServiceLayerClient sapClient,
    ILogger<AssignProductsHandler> logger
) : IRequestHandler<AssignProductsCommand, ErrorOr<MerchandiserProductListResponseDto>>
{
    public async Task<ErrorOr<MerchandiserProductListResponseDto>> Handle(
        AssignProductsCommand command,
        CancellationToken cancellationToken)
    {
        var user = await context.Users
            .FirstOrDefaultAsync(u => u.Id == command.UserId && u.Role == "Merchandiser", cancellationToken);

        if (user == null)
            return Errors.Merchandiser.NotFound(command.UserId);

        if (command.Request.ItemCodes == null || command.Request.ItemCodes.Count == 0)
            return Errors.Merchandiser.AssignmentFailed("At least one item code is required");

        var existing = await context.MerchandiserProducts
            .Where(mp => mp.MerchandiserUserId == command.UserId)
            .Select(mp => mp.ItemCode)
            .ToListAsync(cancellationToken);

        var newCodes = command.Request.ItemCodes.Except(existing).ToList();

        // Fetch product details from local DB + SAP for denormalized fields
        var productDetails = await GetProductDetailsAsync(newCodes, cancellationToken);

        var newEntities = newCodes.Select(code =>
        {
            var details = productDetails.GetValueOrDefault(code);
            return new MerchandiserProductEntity
            {
                MerchandiserUserId = command.UserId,
                ItemCode = code,
                ItemName = details?.ItemName,
                BarCode = details?.BarCode,
                UoM = details?.UoM,
                Category = details?.Category,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedBy = command.Username
            };
        }).ToList();

        context.MerchandiserProducts.AddRange(newEntities);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Assigned {Count} products to merchandiser {UserId}", newCodes.Count, command.UserId);

        return await sender.Send(new GetMerchandiserProductsQuery(command.UserId), cancellationToken);
    }

    private async Task<Dictionary<string, ProductDetailInfo>> GetProductDetailsAsync(
        List<string> itemCodes, CancellationToken cancellationToken)
    {
        if (itemCodes.Count == 0)
            return new();

        // Start with local Products table
        var localProducts = await context.Products
            .AsNoTracking()
            .Where(p => itemCodes.Contains(p.ItemCode))
            .Select(p => new { p.ItemCode, p.ItemName, p.BarCode, UoM = p.SalesUnit ?? p.InventoryUOM })
            .ToDictionaryAsync(p => p.ItemCode, cancellationToken);

        // Fetch category from SAP (U_ItemGroup is not in local Products table)
        Dictionary<string, string?> categories = new();
        try
        {
            var rows = await MerchandiserItemSql.GetItemDetailsAsync(sapClient, itemCodes, cancellationToken);

            return rows.ToDictionary(
                r => r.GetValueOrDefault("ItemCode")?.ToString() ?? "",
                r => new ProductDetailInfo
                {
                    ItemName = r.GetValueOrDefault("ItemName")?.ToString(),
                    BarCode = r.GetValueOrDefault("CodeBars")?.ToString(),
                    UoM = r.GetValueOrDefault("SalUnitMsr")?.ToString(),
                    Category = r.GetValueOrDefault("U_ItemGroup")?.ToString()
                });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch product details from SAP during assignment, using local data");
        }

        // Fallback to local-only data
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
