using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Common.Caching;

public sealed class CacheSyncStateRecorder(
    IServiceScopeFactory scopeFactory,
    ILogger<CacheSyncStateRecorder> logger
)
{
    private const int MaxErrorLength = 1000;

    public Task RecordSuccessAsync(
        string cacheKey,
        string displayName,
        int itemCount,
        DateTime syncedAt,
        CancellationToken cancellationToken = default)
    {
        return UpsertAsync(
            cacheKey,
            displayName,
            cancellationToken,
            state =>
            {
                state.ItemCount = itemCount;
                state.LastSyncedAt = syncedAt;
                state.LastError = null;
                state.LastErrorAt = null;
                state.UpdatedAt = syncedAt;
            });
    }

    public Task RecordFailureAsync(
        string cacheKey,
        string displayName,
        string errorMessage,
        DateTime failedAt,
        CancellationToken cancellationToken = default)
    {
        return UpsertAsync(
            cacheKey,
            displayName,
            cancellationToken,
            state =>
            {
                state.LastError = Truncate(errorMessage, MaxErrorLength);
                state.LastErrorAt = failedAt;
                state.UpdatedAt = failedAt;
            });
    }

    private async Task UpsertAsync(
        string cacheKey,
        string displayName,
        CancellationToken cancellationToken,
        Action<CacheSyncStateEntity> mutate)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var state = await context.CacheSyncStates
                .AsTracking()
                .SingleOrDefaultAsync(entry => entry.CacheKey == cacheKey, cancellationToken);

            if (state is null)
            {
                state = new CacheSyncStateEntity
                {
                    CacheKey = cacheKey,
                    DisplayName = displayName,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                context.CacheSyncStates.Add(state);
            }

            state.DisplayName = displayName;
            mutate(state);

            await context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to record cache sync state for {CacheKey}", cacheKey);
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength];
    }
}