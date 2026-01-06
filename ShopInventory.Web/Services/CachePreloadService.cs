using ShopInventory.Web.Services;

namespace ShopInventory.Web.Services;

/// <summary>
/// Background service that logs cache status on app startup.
/// Note: Cannot preload cache without authentication - cache will be populated
/// when first authenticated user accesses the pages requiring master data.
/// The static cache in MasterDataCacheService ensures data is shared across all circuits.
/// </summary>
public class CachePreloadService : BackgroundService
{
    private readonly ILogger<CachePreloadService> _logger;

    public CachePreloadService(ILogger<CachePreloadService> logger)
    {
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("=== CACHE SERVICE READY ===");
        _logger.LogInformation("Master data cache will be populated when first authenticated user accesses data.");
        _logger.LogInformation("Static cache will then be shared across all Blazor circuits.");
        return Task.CompletedTask;
    }
}
