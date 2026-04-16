using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.Prices.Queries.GetPricesByPriceList;

public sealed class GetPricesByPriceListHandler(
    ISAPServiceLayerClient sapClient,
    ApplicationDbContext context,
    IOptions<SAPSettings> settings,
    ILogger<GetPricesByPriceListHandler> logger
) : IRequestHandler<GetPricesByPriceListQuery, ErrorOr<ItemPricesByListResponseDto>>
{
    public async Task<ErrorOr<ItemPricesByListResponseDto>> Handle(
        GetPricesByPriceListQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.Price.SapDisabled;

        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            var cachedPrices = await context.ItemPrices
                .Where(p => p.PriceList == request.PriceListNum && p.SyncedFromSAP)
                .ToListAsync(cancellationToken);

            var cacheExpiry = TimeSpan.FromMinutes(15);
            var oldestAllowed = DateTime.UtcNow.Subtract(cacheExpiry);

            if (request.ForceRefresh || cachedPrices.Count == 0 ||
                cachedPrices.Any(p => p.LastSyncedAt == null || p.LastSyncedAt < oldestAllowed))
            {
                logger.LogInformation("Syncing prices for price list {PriceListNum} from SAP (forceRefresh={ForceRefresh}, cacheCount={CacheCount})",
                    request.PriceListNum, request.ForceRefresh, cachedPrices.Count);

                await SyncItemPricesFromSAPAsync(request.PriceListNum, cancellationToken);

                cachedPrices = await context.ItemPrices
                    .Where(p => p.PriceList == request.PriceListNum && p.SyncedFromSAP)
                    .ToListAsync(cancellationToken);
            }

            stopwatch.Stop();

            var prices = cachedPrices.Select(p => new ItemPriceByListDto
            {
                ItemCode = p.ItemCode,
                ItemName = p.ItemName,
                ForeignName = p.ForeignName,
                Price = p.Price,
                PriceListNum = p.PriceList,
                PriceListName = p.PriceListName,
                Currency = p.Currency
            }).ToList();

            var priceListEntity = await context.PriceLists
                .FirstOrDefaultAsync(pl => pl.ListNum == request.PriceListNum, cancellationToken);

            var response = new ItemPricesByListResponseDto
            {
                TotalCount = prices.Count,
                PriceListNum = request.PriceListNum,
                PriceListName = priceListEntity?.ListName ?? cachedPrices.FirstOrDefault()?.PriceListName ?? $"Price List {request.PriceListNum}",
                Currency = priceListEntity?.Currency ?? cachedPrices.FirstOrDefault()?.Currency,
                Prices = prices
            };

            logger.LogInformation("Retrieved {Count} item prices from price list {PriceListNum} ({PriceListName}) in {Elapsed}ms (cached)",
                response.TotalCount, request.PriceListNum, response.PriceListName, stopwatch.ElapsedMilliseconds);

            return response;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            logger.LogError(ex, "Timeout connecting to SAP Service Layer");
            return Errors.Price.SapTimeout;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return Errors.Price.SapConnectionError(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving prices from price list {PriceListNum}", request.PriceListNum);
            return Errors.Price.SapError(ex.Message);
        }
    }

    private async Task SyncItemPricesFromSAPAsync(int priceListNum, CancellationToken cancellationToken)
    {
        var sapPrices = await sapClient.GetPricesByPriceListAsync(priceListNum, cancellationToken);

        if (sapPrices.Count == 0)
        {
            logger.LogWarning("No prices returned from SAP for price list {PriceListNum}", priceListNum);
            return;
        }

        var syncTime = DateTime.UtcNow;

        var existingPrices = await context.ItemPrices
            .Where(p => p.PriceList == priceListNum && p.SyncedFromSAP)
            .ToDictionaryAsync(p => p.ItemCode, cancellationToken);

        foreach (var sapPrice in sapPrices)
        {
            if (existingPrices.TryGetValue(sapPrice.ItemCode ?? "", out var existing))
            {
                existing.ItemName = sapPrice.ItemName;
                existing.ForeignName = sapPrice.ForeignName;
                existing.Price = sapPrice.Price;
                existing.PriceListName = sapPrice.PriceListName;
                existing.Currency = sapPrice.Currency;
                existing.LastSyncedAt = syncTime;
                existing.UpdatedAt = syncTime;
            }
            else if (!string.IsNullOrEmpty(sapPrice.ItemCode))
            {
                context.ItemPrices.Add(new ItemPriceEntity
                {
                    PriceList = priceListNum,
                    ItemCode = sapPrice.ItemCode,
                    ItemName = sapPrice.ItemName,
                    ForeignName = sapPrice.ForeignName,
                    Price = sapPrice.Price,
                    PriceListName = sapPrice.PriceListName,
                    Currency = sapPrice.Currency,
                    SyncedFromSAP = true,
                    CreatedAt = syncTime,
                    LastSyncedAt = syncTime
                });
            }
        }

        var sapItemCodes = sapPrices.Where(p => !string.IsNullOrEmpty(p.ItemCode)).Select(p => p.ItemCode!).ToHashSet();
        var toRemove = existingPrices.Values.Where(p => !sapItemCodes.Contains(p.ItemCode)).ToList();
        if (toRemove.Count > 0)
        {
            context.ItemPrices.RemoveRange(toRemove);
        }

        await context.SaveChangesAsync(cancellationToken);
    }
}
