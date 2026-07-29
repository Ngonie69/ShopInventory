using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Prices;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.Prices.Commands.SyncPriceCatalog;

public sealed class SyncPriceCatalogHandler(
    ISAPServiceLayerClient sapClient,
    ApplicationDbContext context,
    IOptions<SAPSettings> settings,
    BackgroundWorkerLeaderElector leaderElector,
    ILogger<SyncPriceCatalogHandler> logger
) : IRequestHandler<SyncPriceCatalogCommand, ErrorOr<object>>
{
    private const int SpecialPriceSaveBatchSize = 250;

    public async Task<ErrorOr<object>> Handle(
        SyncPriceCatalogCommand command,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.Price.SapDisabled;

        using var syncLease = await PriceCatalogSyncGate.TryEnterAsync(cancellationToken);
        if (syncLease is null)
        {
            logger.LogWarning("Skipped full price catalog sync because another SAP price sync is already running");
            return Errors.Price.SyncAlreadyRunning;
        }

        await using var clusterLease = await leaderElector.TryAcquireAsync(PriceCatalogSyncGate.ClusterLockName, cancellationToken);
        if (clusterLease is null)
        {
            logger.LogWarning("Skipped full price catalog sync because another API instance is already running SAP price sync");
            return Errors.Price.SyncAlreadyRunning;
        }

        try
        {
            logger.LogInformation("Starting full price catalog sync from SAP");
            using var priceResolutionScope = sapClient.BeginPriceListResolutionScope();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var syncTime = DateTime.UtcNow;

            var sapPriceLists = await sapClient.GetPriceListsAsync(cancellationToken);
            await SyncPriceListsAsync(sapPriceLists, syncTime, cancellationToken);

            var sapBusinessPartners = await sapClient.GetBusinessPartnersAsync(cancellationToken);
            await SyncBusinessPartnerPriceProfilesAsync(sapBusinessPartners, syncTime, cancellationToken);

            var syncedPriceListCount = 0;
            var syncedItemPriceCount = 0;

            foreach (var priceList in sapPriceLists)
            {
                var sapPrices = await sapClient.GetPricesByPriceListAsync(priceList.ListNum, cancellationToken);
                syncedItemPriceCount += sapPrices.Count;
                syncedPriceListCount++;

                if (sapPrices.Count == 0 && priceList.IsActive)
                {
                    logger.LogWarning(
                        "No item prices returned from SAP for active price list {PriceListNum} (base {BasePriceList}, factor {Factor}); retaining existing cached prices for this list",
                        priceList.ListNum,
                        priceList.BasePriceList,
                        priceList.Factor);
                    continue;
                }

                await SyncItemPricesForPriceListAsync(priceList.ListNum, sapPrices, syncTime, cancellationToken);
            }

            var activePriceListNumbers = sapPriceLists.Select(priceList => priceList.ListNum).ToHashSet();
            await context.ItemPrices
                .Where(price => price.SyncedFromSAP && !activePriceListNumbers.Contains(price.PriceList))
                .ExecuteDeleteAsync(cancellationToken);

            var sapSpecialPrices = await sapClient.GetAllSpecialPricesAsync(cancellationToken);
            await SyncBusinessPartnerSpecialPricesAsync(sapSpecialPrices, syncTime, cancellationToken);

            stopwatch.Stop();

            logger.LogInformation(
                "Full price catalog sync completed in {Elapsed}ms: {PriceListCount} price lists, {ItemPriceCount} item prices, {SpecialPriceCount} special prices",
                stopwatch.ElapsedMilliseconds,
                syncedPriceListCount,
                syncedItemPriceCount,
                sapSpecialPrices.Count);

            return new
            {
                message = "Price catalog synced successfully",
                priceListCount = syncedPriceListCount,
                businessPartnerProfileCount = sapBusinessPartners.Count,
                itemPriceCount = syncedItemPriceCount,
                specialPriceCount = sapSpecialPrices.Count,
                syncedAt = syncTime
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Price catalog sync was canceled");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error syncing full price catalog from SAP");
            return Errors.Price.SapError(ex.Message);
        }
    }

    private async Task SyncPriceListsAsync(
        IReadOnlyCollection<PriceListDto> sapPriceLists,
        DateTime syncTime,
        CancellationToken cancellationToken)
    {
        var existingPriceLists = await context.PriceLists
            .AsTracking()
            .ToDictionaryAsync(priceList => priceList.ListNum, cancellationToken);

        var activeListNumbers = sapPriceLists.Select(priceList => priceList.ListNum).ToHashSet();

        foreach (var sapList in sapPriceLists)
        {
            if (existingPriceLists.TryGetValue(sapList.ListNum, out var existing))
            {
                existing.ListName = sapList.ListName;
                existing.BasePriceList = int.TryParse(sapList.BasePriceList, out var basePriceList) ? basePriceList : null;
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
                    BasePriceList = int.TryParse(sapList.BasePriceList, out var basePriceList) ? basePriceList : null,
                    Currency = sapList.Currency,
                    Factor = sapList.Factor,
                    RoundingMethod = sapList.RoundingMethod,
                    IsActive = sapList.IsActive,
                    CreatedAt = syncTime,
                    LastSyncedAt = syncTime
                });
            }
        }

        foreach (var stalePriceList in existingPriceLists.Values.Where(priceList => !activeListNumbers.Contains(priceList.ListNum)))
        {
            stalePriceList.IsActive = false;
            stalePriceList.UpdatedAt = syncTime;
            stalePriceList.LastSyncedAt = syncTime;
            stalePriceList.ItemCount = 0;
        }

        await context.SaveChangesAsync(cancellationToken);
        context.ChangeTracker.Clear();
    }

    private async Task SyncItemPricesForPriceListAsync(
        int priceListNum,
        IReadOnlyCollection<ItemPriceByListDto> sapPrices,
        DateTime syncTime,
        CancellationToken cancellationToken)
    {
        var existingPrices = await context.ItemPrices
            .AsTracking()
            .Where(price => price.PriceList == priceListNum && price.SyncedFromSAP)
            .ToDictionaryAsync(price => price.ItemCode, cancellationToken);

        foreach (var sapPrice in sapPrices)
        {
            if (string.IsNullOrWhiteSpace(sapPrice.ItemCode))
            {
                continue;
            }

            if (existingPrices.TryGetValue(sapPrice.ItemCode, out var existing))
            {
                existing.ItemName = sapPrice.ItemName;
                existing.ForeignName = sapPrice.ForeignName;
                existing.Price = sapPrice.Price;
                existing.PriceListName = sapPrice.PriceListName;
                existing.Currency = sapPrice.Currency;
                existing.BasePriceList = sapPrice.BasePriceList;
                existing.Factor = sapPrice.Factor;
                existing.IsActive = true;
                existing.LastSyncedAt = syncTime;
                existing.UpdatedAt = syncTime;
            }
            else
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
                    BasePriceList = sapPrice.BasePriceList,
                    Factor = sapPrice.Factor,
                    IsActive = true,
                    SyncedFromSAP = true,
                    CreatedAt = syncTime,
                    LastSyncedAt = syncTime
                });
            }
        }

        var sapItemCodes = sapPrices
            .Where(price => !string.IsNullOrWhiteSpace(price.ItemCode))
            .Select(price => price.ItemCode!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var toRemove = existingPrices.Values
            .Where(price => !sapItemCodes.Contains(price.ItemCode))
            .ToList();

        if (toRemove.Count > 0)
        {
            context.ItemPrices.RemoveRange(toRemove);
        }

        var priceListEntity = await context.PriceLists
            .AsTracking()
            .SingleOrDefaultAsync(priceList => priceList.ListNum == priceListNum, cancellationToken);

        if (priceListEntity is not null)
        {
            priceListEntity.ItemCount = sapPrices.Count;
            priceListEntity.UpdatedAt = syncTime;
            priceListEntity.LastSyncedAt = syncTime;
        }

        await context.SaveChangesAsync(cancellationToken);
        context.ChangeTracker.Clear();
    }

    private async Task SyncBusinessPartnerPriceProfilesAsync(
        IReadOnlyCollection<BusinessPartnerDto> sapBusinessPartners,
        DateTime syncTime,
        CancellationToken cancellationToken)
    {
        var existingProfiles = await context.BusinessPartnerPriceProfiles
            .AsTracking()
            .ToDictionaryAsync(profile => profile.CardCode, StringComparer.OrdinalIgnoreCase, cancellationToken);

        foreach (var businessPartner in sapBusinessPartners)
        {
            var cardCode = businessPartner.CardCode?.Trim();
            if (string.IsNullOrWhiteSpace(cardCode))
            {
                continue;
            }

            var priceListNum = businessPartner.PriceListNum ?? 1;

            if (existingProfiles.TryGetValue(cardCode, out var existingProfile))
            {
                existingProfile.CardName = businessPartner.CardName;
                existingProfile.Currency = businessPartner.Currency;
                existingProfile.PriceListNum = priceListNum;
                existingProfile.IsActive = businessPartner.IsActive;
                existingProfile.UpdatedAt = syncTime;
                existingProfile.LastSyncedAt = syncTime;
            }
            else
            {
                context.BusinessPartnerPriceProfiles.Add(new BusinessPartnerPriceProfileEntity
                {
                    CardCode = cardCode,
                    CardName = businessPartner.CardName,
                    Currency = businessPartner.Currency,
                    PriceListNum = priceListNum,
                    IsActive = businessPartner.IsActive,
                    SyncedFromSAP = true,
                    CreatedAt = syncTime,
                    LastSyncedAt = syncTime
                });
            }
        }

        var sapCardCodes = sapBusinessPartners
            .Select(partner => partner.CardCode?.Trim())
            .Where(cardCode => !string.IsNullOrWhiteSpace(cardCode))
            .Select(cardCode => cardCode!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var staleProfiles = existingProfiles.Values
            .Where(profile => !sapCardCodes.Contains(profile.CardCode))
            .ToList();

        if (staleProfiles.Count > 0)
        {
            context.BusinessPartnerPriceProfiles.RemoveRange(staleProfiles);
        }

        await context.SaveChangesAsync(cancellationToken);
        context.ChangeTracker.Clear();
    }

    private async Task SyncBusinessPartnerSpecialPricesAsync(
        IReadOnlyCollection<BusinessPartnerSpecialPriceDto> sapSpecialPrices,
        DateTime syncTime,
        CancellationToken cancellationToken)
    {
        var existingSpecialPrices = await context.BusinessPartnerSpecialPrices
            .AsTracking()
            .ToDictionaryAsync(
                price => $"{price.CardCode}::{price.ItemCode}",
                StringComparer.OrdinalIgnoreCase,
                cancellationToken);

        var pendingChanges = 0;
        var persistedBatchCount = 0;

        foreach (var sapPrice in sapSpecialPrices)
        {
            var key = $"{sapPrice.CardCode}::{sapPrice.ItemCode}";
            var validFromUtc = NormalizeUtcDate(sapPrice.ValidFrom);
            var validToUtc = NormalizeUtcDate(sapPrice.ValidTo);
            if (existingSpecialPrices.TryGetValue(key, out var existing))
            {
                existing.Price = sapPrice.Price;
                existing.ValidFrom = validFromUtc;
                existing.ValidTo = validToUtc;
                existing.IsActive = sapPrice.IsActive;
                existing.LastSyncedAt = syncTime;
                existing.UpdatedAt = syncTime;
            }
            else
            {
                context.BusinessPartnerSpecialPrices.Add(new BusinessPartnerSpecialPriceEntity
                {
                    CardCode = sapPrice.CardCode,
                    ItemCode = sapPrice.ItemCode,
                    Price = sapPrice.Price,
                    ValidFrom = validFromUtc,
                    ValidTo = validToUtc,
                    IsActive = sapPrice.IsActive,
                    SyncedFromSAP = true,
                    CreatedAt = syncTime,
                    LastSyncedAt = syncTime
                });
            }

            pendingChanges++;
            if (pendingChanges >= SpecialPriceSaveBatchSize)
            {
                await context.SaveChangesAsync(cancellationToken);
                pendingChanges = 0;
                persistedBatchCount++;
            }
        }

        if (pendingChanges > 0)
        {
            await context.SaveChangesAsync(cancellationToken);
            persistedBatchCount++;
        }

        var sapKeys = sapSpecialPrices
            .Select(price => $"{price.CardCode}::{price.ItemCode}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var toRemove = existingSpecialPrices.Values
            .Where(price => !sapKeys.Contains($"{price.CardCode}::{price.ItemCode}"))
            .ToList();

        foreach (var removalBatch in toRemove.Chunk(SpecialPriceSaveBatchSize))
        {
            context.BusinessPartnerSpecialPrices.RemoveRange(removalBatch);
            await context.SaveChangesAsync(cancellationToken);
            persistedBatchCount++;
        }

        context.ChangeTracker.Clear();

        logger.LogInformation(
            "Persisted {SpecialPriceCount} SAP special prices in {BatchCount} bounded database batches; removed {RemovedCount} stale records",
            sapSpecialPrices.Count,
            persistedBatchCount,
            toRemove.Count);
    }

    private static DateTime? NormalizeUtcDate(DateTime? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        var utcValue = value.Value.Kind switch
        {
            DateTimeKind.Utc => value.Value,
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
        };

        return DateTime.SpecifyKind(utcValue.Date, DateTimeKind.Utc);
    }
}
