using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.Prices.Commands.SyncItemPricesForPriceList;

public sealed class SyncItemPricesForPriceListHandler(
    ISAPServiceLayerClient sapClient,
    ApplicationDbContext context,
    IOptions<SAPSettings> settings,
    ILogger<SyncItemPricesForPriceListHandler> logger
) : IRequestHandler<SyncItemPricesForPriceListCommand, ErrorOr<object>>
{
    public async Task<ErrorOr<object>> Handle(
        SyncItemPricesForPriceListCommand command,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.Price.SapDisabled;

        try
        {
            logger.LogInformation("Starting item price sync for price list {PriceListNum}...", command.PriceListNum);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            var sapPrices = await sapClient.GetPricesByPriceListAsync(command.PriceListNum, cancellationToken);
            if (sapPrices.Count == 0)
            {
                logger.LogWarning("No prices returned from SAP for price list {PriceListNum}", command.PriceListNum);
                return new
                {
                    message = "No prices returned from SAP",
                    count = 0,
                    priceListNum = command.PriceListNum,
                    syncedAt = DateTime.UtcNow
                };
            }

            var syncTime = DateTime.UtcNow;
            var existingPrices = await context.ItemPrices
                .AsTracking()
                .Where(p => p.PriceList == command.PriceListNum && p.SyncedFromSAP)
                .ToDictionaryAsync(p => p.ItemCode, cancellationToken);

            foreach (var sapPrice in sapPrices)
            {
                if (string.IsNullOrEmpty(sapPrice.ItemCode))
                    continue;

                if (existingPrices.TryGetValue(sapPrice.ItemCode, out var existing))
                {
                    existing.ItemName = sapPrice.ItemName;
                    existing.ForeignName = sapPrice.ForeignName;
                    existing.Price = sapPrice.Price;
                    existing.PriceListName = sapPrice.PriceListName;
                    existing.Currency = sapPrice.Currency;
                    existing.LastSyncedAt = syncTime;
                    existing.UpdatedAt = syncTime;
                }
                else
                {
                    context.ItemPrices.Add(new ItemPriceEntity
                    {
                        PriceList = command.PriceListNum,
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

            var sapItemCodes = sapPrices
                .Where(p => !string.IsNullOrEmpty(p.ItemCode))
                .Select(p => p.ItemCode!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var toRemove = existingPrices.Values
                .Where(p => !sapItemCodes.Contains(p.ItemCode))
                .ToList();

            if (toRemove.Count > 0)
            {
                context.ItemPrices.RemoveRange(toRemove);
            }

            await context.SaveChangesAsync(cancellationToken);
            stopwatch.Stop();

            logger.LogInformation("Item price sync completed for price list {PriceListNum}: {Count} prices in {Elapsed}ms",
                command.PriceListNum, sapPrices.Count, stopwatch.ElapsedMilliseconds);

            return new
            {
                message = "Item prices synced successfully",
                count = sapPrices.Count,
                priceListNum = command.PriceListNum,
                syncedAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error syncing item prices for price list {PriceListNum}", command.PriceListNum);
            return Errors.Price.SapError(ex.Message);
        }
    }
}
