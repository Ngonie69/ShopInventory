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

namespace ShopInventory.Features.Prices.Queries.GetPriceLists;

public sealed class GetPriceListsHandler(
    ISAPServiceLayerClient sapClient,
    ApplicationDbContext context,
    IOptions<SAPSettings> settings,
    ILogger<GetPriceListsHandler> logger
) : IRequestHandler<GetPriceListsQuery, ErrorOr<PriceListsResponseDto>>
{
    public async Task<ErrorOr<PriceListsResponseDto>> Handle(
        GetPriceListsQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            var cachedCount = await context.PriceLists.CountAsync(cancellationToken);
            var lastSync = await context.PriceLists
                .Where(p => p.LastSyncedAt.HasValue)
                .MaxAsync(p => (DateTime?)p.LastSyncedAt, cancellationToken);

            var cacheExpiry = TimeSpan.FromHours(24);
            var needsSync = request.ForceRefresh ||
                           cachedCount == 0 ||
                           !lastSync.HasValue ||
                           lastSync.Value < DateTime.UtcNow.Subtract(cacheExpiry);

            if (needsSync && settings.Value.Enabled)
            {
                logger.LogInformation("Syncing price lists from SAP (forceRefresh={ForceRefresh}, cacheCount={Count}, lastSync={LastSync})",
                    request.ForceRefresh, cachedCount, lastSync);

                try
                {
                    await SyncPriceListsFromSAPAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to sync price lists from SAP, returning cached data");
                    if (cachedCount == 0)
                        throw;
                }
            }

            var priceLists = await context.PriceLists
                .Where(p => p.IsActive)
                .OrderBy(p => p.ListNum)
                .Select(p => new PriceListDto
                {
                    ListNum = p.ListNum,
                    ListName = p.ListName,
                    BasePriceList = p.BasePriceList.HasValue ? p.BasePriceList.Value.ToString() : null,
                    Currency = p.Currency,
                    IsActive = p.IsActive,
                    Factor = p.Factor,
                    RoundingMethod = p.RoundingMethod
                })
                .ToListAsync(cancellationToken);

            stopwatch.Stop();

            var response = new PriceListsResponseDto
            {
                TotalCount = priceLists.Count,
                PriceLists = priceLists
            };

            logger.LogInformation("Retrieved {Count} price lists in {Elapsed}ms (last sync: {LastSync}, needsSync: {NeedsSync})",
                response.TotalCount, stopwatch.ElapsedMilliseconds, lastSync?.ToString("yyyy-MM-dd HH:mm:ss") ?? "Never", needsSync);

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
            logger.LogError(ex, "Error retrieving price lists");
            return Errors.Price.SapError(ex.Message);
        }
    }

    private async Task SyncPriceListsFromSAPAsync(CancellationToken cancellationToken)
    {
        var sapPriceLists = await sapClient.GetPriceListsAsync(cancellationToken);
        var syncTime = DateTime.UtcNow;

        foreach (var sapList in sapPriceLists)
        {
            var existing = await context.PriceLists
                .FirstOrDefaultAsync(p => p.ListNum == sapList.ListNum, cancellationToken);

            if (existing != null)
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
    }
}
