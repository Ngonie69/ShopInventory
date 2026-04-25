using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.Prices.Commands.SyncPriceLists;

public sealed class SyncPriceListsHandler(
    ISAPServiceLayerClient sapClient,
    ApplicationDbContext context,
    IOptions<SAPSettings> settings,
    ILogger<SyncPriceListsHandler> logger
) : IRequestHandler<SyncPriceListsCommand, ErrorOr<object>>
{
    public async Task<ErrorOr<object>> Handle(
        SyncPriceListsCommand command,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.Price.SapDisabled;

        try
        {
            logger.LogInformation("Starting price list sync from SAP...");
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            var sapPriceLists = await sapClient.GetPriceListsAsync(cancellationToken);
            var syncTime = DateTime.UtcNow;
            var existingPriceLists = await context.PriceLists
                .AsTracking()
                .ToDictionaryAsync(p => p.ListNum, cancellationToken);

            foreach (var sapList in sapPriceLists)
            {
                if (existingPriceLists.TryGetValue(sapList.ListNum, out var existing))
                {
                    existing.ListName = sapList.ListName;
                    existing.Currency = sapList.Currency;
                    existing.Factor = sapList.Factor;
                    existing.RoundingMethod = sapList.RoundingMethod;
                    existing.IsActive = sapList.IsActive;
                    existing.UpdatedAt = syncTime;
                    existing.LastSyncedAt = syncTime;
                }
                else
                {
                    context.PriceLists.Add(new PriceListEntity
                    {
                        ListNum = sapList.ListNum,
                        ListName = sapList.ListName,
                        Currency = sapList.Currency,
                        Factor = sapList.Factor,
                        RoundingMethod = sapList.RoundingMethod,
                        IsActive = sapList.IsActive,
                        CreatedAt = syncTime,
                        LastSyncedAt = syncTime
                    });
                }
            }

            await context.SaveChangesAsync(cancellationToken);
            stopwatch.Stop();

            logger.LogInformation("Price list sync completed: {Count} lists synced in {Elapsed}ms",
                sapPriceLists.Count, stopwatch.ElapsedMilliseconds);

            return new
            {
                message = "Price lists synced successfully",
                count = sapPriceLists.Count,
                syncedAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error syncing price lists from SAP");
            return Errors.Price.SapError(ex.Message);
        }
    }
}
