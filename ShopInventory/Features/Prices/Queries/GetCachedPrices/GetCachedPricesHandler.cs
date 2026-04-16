using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Prices.Queries.GetCachedPrices;

public sealed class GetCachedPricesHandler(
    ApplicationDbContext context,
    ILogger<GetCachedPricesHandler> logger
) : IRequestHandler<GetCachedPricesQuery, ErrorOr<object>>
{
    public async Task<ErrorOr<object>> Handle(
        GetCachedPricesQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var prices = await context.ItemPrices
                .Where(p => p.IsActive && p.SyncedFromSAP)
                .OrderBy(p => p.ItemCode)
                .Select(p => new ItemPriceDto
                {
                    ItemCode = p.ItemCode,
                    ItemName = p.ItemName,
                    Price = p.Price,
                    Currency = p.Currency
                })
                .ToListAsync(cancellationToken);

            var lastSync = await context.ItemPrices
                .Where(p => p.SyncedFromSAP && p.LastSyncedAt.HasValue)
                .MaxAsync(p => (DateTime?)p.LastSyncedAt, cancellationToken);

            logger.LogInformation("Retrieved {Count} cached item prices. Last sync: {LastSync}",
                prices.Count, lastSync?.ToString("yyyy-MM-dd HH:mm:ss") ?? "Never");

            return new
            {
                TotalCount = prices.Count,
                UsdPriceCount = prices.Count(p => p.Currency == "USD"),
                ZigPriceCount = prices.Count(p => p.Currency == "ZIG"),
                LastSyncedAt = lastSync,
                Prices = prices
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving cached item prices");
            return Errors.Price.SapError(ex.Message);
        }
    }
}
