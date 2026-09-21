using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Common.Caching;

public sealed class CacheSyncStateRecorder(
    IServiceScopeFactory scopeFactory,
    ILogger<CacheSyncStateRecorder> logger
)
{
    private const int MaxErrorLength = 1000;

    // publishSyncEvent: whether this sync also raises the sap.sync.success webhook. Opt-in per call
    // rather than inferred from the cache key, because this recorder serves Cartrack's fleet sync as
    // well and that one is not SAP. A new SAP-backed cache has to say so here to be heard.
    public async Task RecordSuccessAsync(
        string cacheKey,
        string displayName,
        int itemCount,
        DateTime syncedAt,
        CancellationToken cancellationToken = default,
        bool publishSyncEvent = false)
    {
        await UpsertAsync(
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

        if (publishSyncEvent)
        {
            await PublishAsync(
                WebhookEventTypes.SapSyncSuccess,
                new { cacheKey, displayName, itemCount, syncedAt });
        }
    }

    // publishSyncEvent: see the note on RecordSuccessAsync.
    public async Task RecordFailureAsync(
        string cacheKey,
        string displayName,
        string errorMessage,
        DateTime failedAt,
        CancellationToken cancellationToken = default,
        bool publishSyncEvent = false)
    {
        var recordedError = Truncate(errorMessage, MaxErrorLength);

        await UpsertAsync(
            cacheKey,
            displayName,
            cancellationToken,
            state =>
            {
                state.LastError = recordedError;
                state.LastErrorAt = failedAt;
                state.UpdatedAt = failedAt;
            });

        if (publishSyncEvent)
        {
            // The truncated message, so a subscriber and the sync dashboard describe the same failure
            // and neither carries a stack trace the size of a page.
            await PublishAsync(
                WebhookEventTypes.SapSyncFailed,
                new { cacheKey, displayName, error = recordedError, failedAt });
        }
    }

    /// <summary>
    /// Raises a webhook event on its own scope, because this class is a singleton.
    /// </summary>
    /// <remarks>
    /// Swallowing is deliberate and matches the state write below: a SAP read that worked is not
    /// turned into a failure by a webhook that could not be raised, and the caller here is usually
    /// inside a catch that is about to rethrow.
    /// </remarks>
    private async Task PublishAsync(string eventType, object payload)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var webhooks = scope.ServiceProvider.GetRequiredService<IWebhookService>();
            await webhooks.TriggerEventAsync(eventType, payload);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to publish {EventType}", eventType);
        }
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