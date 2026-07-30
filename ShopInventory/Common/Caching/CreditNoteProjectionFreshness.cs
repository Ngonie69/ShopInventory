using ShopInventory.Configuration;
using ShopInventory.Models.Entities;

namespace ShopInventory.Common.Caching;

/// <summary>
/// Whether the local SAP credit-note projection is current enough to answer a read instead of
/// going to SAP. Shared so the POD report and the credit-note list cannot drift apart on it.
/// </summary>
public static class CreditNoteProjectionFreshness
{
    /// <summary>
    /// Fresh means the sync completed inside the staleness window and has not failed since. A
    /// failure recorded after the last success is treated as stale even while inside the window:
    /// the projection may be missing whatever that run was carrying.
    /// </summary>
    public static bool IsFresh(
        CacheSyncStateEntity? syncState,
        CreditNoteSyncSettings settings,
        DateTime nowUtc)
    {
        if (syncState?.LastSyncedAt is not { } lastSyncedAt)
        {
            return false;
        }

        var hasCurrentError = syncState.LastErrorAt.HasValue &&
            syncState.LastErrorAt.Value >= lastSyncedAt;

        return !hasCurrentError &&
            nowUtc - lastSyncedAt <= TimeSpan.FromMinutes(Math.Max(1, settings.StaleAfterMinutes));
    }
}
