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

namespace ShopInventory.Features.Prices.Commands.SyncSpecialPrices;

public sealed class SyncSpecialPricesHandler(
    ISAPServiceLayerClient sapClient,
    ApplicationDbContext context,
    IOptions<SAPSettings> settings,
    BackgroundWorkerLeaderElector leaderElector,
    ILogger<SyncSpecialPricesHandler> logger
) : IRequestHandler<SyncSpecialPricesCommand, ErrorOr<SpecialPriceSyncResult>>
{
    private const int SpecialPriceSaveBatchSize = 250;

    public async Task<ErrorOr<SpecialPriceSyncResult>> Handle(
        SyncSpecialPricesCommand command,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.Price.SapDisabled;

        using var syncLease = await PriceCatalogSyncGate.SpecialPrices.TryEnterAsync(cancellationToken);
        if (syncLease is null)
        {
            logger.LogWarning("Skipped special price sync because another special price sync is already running");
            return Errors.Price.SpecialPriceSyncAlreadyRunning;
        }

        await using var clusterLease = await leaderElector.TryAcquireAsync(PriceCatalogSyncGate.SpecialPrices.ClusterLockName, cancellationToken);
        if (clusterLease is null)
        {
            logger.LogWarning("Skipped special price sync because another API instance is already running it");
            return Errors.Price.SpecialPriceSyncAlreadyRunning;
        }

        try
        {
            logger.LogInformation("Starting special price sync from SAP");
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var syncTime = DateTime.UtcNow;

            var sapSpecialPrices = await sapClient.GetAllSpecialPricesAsync(cancellationToken);
            var removedCount = await SyncBusinessPartnerSpecialPricesAsync(sapSpecialPrices, syncTime, cancellationToken);

            stopwatch.Stop();

            logger.LogInformation(
                "Special price sync completed in {Elapsed}ms: {SpecialPriceCount} special prices, {RemovedCount} removed",
                stopwatch.ElapsedMilliseconds,
                sapSpecialPrices.Count,
                removedCount);

            return new SpecialPriceSyncResult(sapSpecialPrices.Count, removedCount, syncTime);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Special price sync was canceled");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error syncing special prices from SAP");
            return Errors.Price.SapError(ex.Message);
        }
    }

    private async Task<int> SyncBusinessPartnerSpecialPricesAsync(
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

        return toRemove.Count;
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
