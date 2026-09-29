using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Web.Data;

namespace ShopInventory.Web.Services;

/// <summary>
/// Decides when a cache is due a refresh and lets one refresh per cache key run at a time.
/// </summary>
/// <remarks>
/// The payment, transfer and warehouse stock caches refresh from the read that finds them stale. Each
/// such read used to queue a refresh of its own behind one semaphore, and a queued refresh never looked
/// at whether the one ahead of it had just done the work: five reads while a sweep ran were five full
/// sweeps of SAP in a row. A read now claims the key before starting anything, and a read that finds it
/// claimed serves the cached rows, which the refresh already under way will update.
/// </remarks>
public static class CacheRefresh
{
    /// <summary>
    /// After a failed refresh, how long reads serve the cached rows before one tries again.
    /// </summary>
    /// <remarks>
    /// A failure used to make the cache stale for every following read, so while SAP or the API was
    /// down each page view started another refresh straight after the last one failed.
    /// </remarks>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(2);

    private static readonly ConcurrentDictionary<string, byte> Claimed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when the cache has never been refreshed, its last refresh is older than
    /// <paramref name="expiry"/>, or its last refresh failed more than <see cref="RetryAfterFailure"/> ago.
    /// </summary>
    public static bool IsDue(CacheSyncInfo? syncInfo, TimeSpan expiry, DateTime utcNow) =>
        syncInfo is null
        || utcNow - syncInfo.LastSyncedAt > (syncInfo.SyncSuccessful ? expiry : RetryAfterFailure);

    /// <summary>
    /// Starts <paramref name="refresh"/> in the background as SAP background work, unless a refresh of
    /// <paramref name="key"/> is already queued or running. Returns whether it started one.
    /// </summary>
    public static bool StartInBackground(string key, Func<Task> refresh)
    {
        if (!Claimed.TryAdd(key, 0))
            return false;

        _ = SapBackgroundPriority.Run(async () =>
        {
            try
            {
                await refresh();
            }
            finally
            {
                Claimed.TryRemove(key, out _);
            }
        });

        return true;
    }

    /// <summary>
    /// Runs <paramref name="refresh"/> now, unless a refresh of <paramref name="key"/> is already queued
    /// or running, in which case it returns false without waiting for it.
    /// </summary>
    public static async Task<bool> RunNowAsync(string key, Func<Task<bool>> refresh)
    {
        if (!Claimed.TryAdd(key, 0))
            return false;

        try
        {
            // Every other caller skipped because this one claimed the key, so it must finish for
            // them even if its own page is left (see PageReads).
            using var sharedLoad = PageReads.Detach();
            return await refresh();
        }
        finally
        {
            Claimed.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// The cache key under which a document cache keeps its watermark.
    /// </summary>
    public static string WatermarkKey(string cacheKey) => $"{cacheKey}:Watermark";

    /// <summary>
    /// The highest DocEntry a document cache has read SAP through without a gap, or null before its first
    /// full walk.
    /// </summary>
    /// <remarks>
    /// Kept in its own <c>CacheSyncInfo</c> row, in <c>ItemCount</c>. It is not the highest DocEntry in
    /// the cache: opening one document on its own caches it ahead of the refresh, and reading only past
    /// that one would skip every document between it and the last refresh for good.
    /// </remarks>
    public static async Task<int?> ReadWatermarkAsync(
        IDbContextFactory<WebAppDbContext> dbContextFactory,
        string cacheKey)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        var key = WatermarkKey(cacheKey);
        var row = await dbContext.CacheSyncInfo
            .AsNoTracking()
            .FirstOrDefaultAsync(info => info.CacheKey == key);

        return row?.ItemCount;
    }

    /// <summary>
    /// Records that the cache now holds every document up to <paramref name="docEntry"/>.
    /// </summary>
    public static async Task WriteWatermarkAsync(
        IDbContextFactory<WebAppDbContext> dbContextFactory,
        string cacheKey,
        int docEntry)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        var key = WatermarkKey(cacheKey);
        var row = await dbContext.CacheSyncInfo.FindAsync(key);
        if (row is null)
        {
            row = new CacheSyncInfo { CacheKey = key };
            dbContext.CacheSyncInfo.Add(row);
        }

        row.ItemCount = docEntry;
        row.LastSyncedAt = DateTime.UtcNow;
        row.SyncSuccessful = true;
        row.LastError = null;

        await dbContext.SaveChangesAsync();
    }
}
