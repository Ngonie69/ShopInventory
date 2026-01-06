using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// Background service that syncs item prices from SAP at regular intervals (default: 5 minutes)
/// </summary>
public class PriceSyncBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PriceSyncBackgroundService> _logger;
    private readonly TimeSpan _syncInterval;
    private readonly bool _enabled;

    public PriceSyncBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<PriceSyncBackgroundService> logger,
        IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;

        // Get sync interval from configuration (default: 5 minutes)
        var intervalMinutes = configuration.GetValue<int>("PriceSync:IntervalMinutes", 5);
        _syncInterval = TimeSpan.FromMinutes(intervalMinutes);

        // Check if SAP integration is enabled
        _enabled = configuration.GetValue<bool>("SAP:Enabled", true);

        _logger.LogInformation("Price sync service initialized. Interval: {Interval} minutes, Enabled: {Enabled}",
            intervalMinutes, _enabled);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogWarning("Price sync service is disabled because SAP integration is disabled");
            return;
        }

        _logger.LogInformation("Price sync background service started. First sync will run in 30 seconds...");

        // Wait 30 seconds before first sync to allow app to fully start
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncPricesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Price sync service is stopping");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during price sync. Will retry in {Interval} minutes", _syncInterval.TotalMinutes);
            }

            // Wait for the next sync interval
            try
            {
                await Task.Delay(_syncInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("Price sync background service stopped");
    }

    private async Task SyncPricesAsync(CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("Starting price sync from SAP...");

        using var scope = _serviceProvider.CreateScope();
        var sapClient = scope.ServiceProvider.GetRequiredService<ISAPServiceLayerClient>();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        try
        {
            // Fetch prices from SAP
            var sapPrices = await sapClient.GetItemPricesAsync(cancellationToken);

            if (sapPrices == null || sapPrices.Count == 0)
            {
                _logger.LogWarning("No prices received from SAP");
                return;
            }

            _logger.LogInformation("Received {Count} prices from SAP, updating database...", sapPrices.Count);

            var syncTime = DateTime.UtcNow;
            var updatedCount = 0;
            var insertedCount = 0;

            // Get all existing prices for efficient lookup
            // Key: ItemCode_Currency (since we don't have PriceList in DTO)
            var existingPrices = await context.ItemPrices
                .Where(p => p.SyncedFromSAP)
                .ToDictionaryAsync(
                    p => $"{p.ItemCode}_{p.Currency}",
                    p => p,
                    cancellationToken);

            foreach (var sapPrice in sapPrices)
            {
                if (string.IsNullOrEmpty(sapPrice.ItemCode))
                    continue;

                var key = $"{sapPrice.ItemCode}_{sapPrice.Currency}";

                if (existingPrices.TryGetValue(key, out var existingPrice))
                {
                    // Update existing price
                    existingPrice.ItemName = sapPrice.ItemName;
                    existingPrice.Price = sapPrice.Price;
                    existingPrice.Currency = sapPrice.Currency;
                    existingPrice.UpdatedAt = syncTime;
                    existingPrice.LastSyncedAt = syncTime;
                    existingPrice.IsActive = true;
                    updatedCount++;
                }
                else
                {
                    // Find product ID if exists
                    var productId = await context.Products
                        .Where(p => p.ItemCode == sapPrice.ItemCode)
                        .Select(p => (int?)p.Id)
                        .FirstOrDefaultAsync(cancellationToken);

                    // Insert new price
                    context.ItemPrices.Add(new ItemPriceEntity
                    {
                        ProductId = productId,
                        ItemCode = sapPrice.ItemCode,
                        ItemName = sapPrice.ItemName,
                        Price = sapPrice.Price,
                        Currency = sapPrice.Currency,
                        CreatedAt = syncTime,
                        LastSyncedAt = syncTime,
                        SyncedFromSAP = true,
                        IsActive = true
                    });
                    insertedCount++;
                }
            }

            // Mark prices not in SAP as inactive (soft delete)
            var sapItemKeys = sapPrices
                .Where(p => !string.IsNullOrEmpty(p.ItemCode))
                .Select(p => $"{p.ItemCode}_{p.Currency}")
                .ToHashSet();

            var pricesToDeactivate = existingPrices
                .Where(kvp => !sapItemKeys.Contains(kvp.Key))
                .Select(kvp => kvp.Value)
                .ToList();

            foreach (var price in pricesToDeactivate)
            {
                price.IsActive = false;
                price.UpdatedAt = syncTime;
            }

            await context.SaveChangesAsync(cancellationToken);

            stopwatch.Stop();
            _logger.LogInformation(
                "Price sync completed in {ElapsedMs}ms. Updated: {Updated}, Inserted: {Inserted}, Deactivated: {Deactivated}",
                stopwatch.ElapsedMilliseconds, updatedCount, insertedCount, pricesToDeactivate.Count);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Failed to sync prices from SAP after {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);
            throw;
        }
    }
}
