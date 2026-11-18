using Microsoft.Extensions.Caching.Memory;
using ShopInventory.Middleware;
using ShopInventory.Models;

namespace ShopInventory.Services;

/// <summary>
/// The whole-warehouse batch read the catalogue pages are built from, kept as the last good answer and
/// refreshed behind the page rather than in front of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> On 2026-11-12, 06:54–12:56, 65 of the 124 SAP reads the Service Layer got stuck on were
/// this read for a van (<c>SQLQueries('WHS_BATCHES_VAN004')/List</c> and its siblings): 17 % of the 376
/// sent. The van handset asks for its catalogue every 33 s at the median, the snapshot lasted two
/// minutes, and a page that found it expired waited on a fresh read — which, stuck, failed the page
/// after 60 s and was not kept, so the next page sent the same read again while SAP was still working on
/// the first. 23 of the 65 were sent like that, inside the stuck-read hold, because a person was waiting
/// on them and people's reads are never held. 89 of the 90 pages that failed had a good read of the same
/// van under an hour old.
/// </para>
/// <para>
/// <b>Now.</b> A read is current for <c>SAP:WarehouseBatchSnapshotFreshMinutes</c> (10). After that, a
/// page is answered from it at once, marked stale, and one refresh is sent in the background, at
/// background priority, so the stuck-read hold and the read family's background limit apply to it. It is
/// kept to answer pages for <c>SAP:WarehouseBatchSnapshotMaxStaleMinutes</c> (60); past that, or with none
/// at all, the page waits for SAP as before, and that read is marked display-only so a repeat of a held
/// read is refused at once instead of becoming another stuck Service Layer process.
/// </para>
/// <para>
/// <b>Still right on the handset.</b> The handset takes off what SAP has not yet taken
/// (<c>quantityAwaitingSap</c>), and that is counted from <see cref="WarehouseBatchSnapshot.ReadAtUtc"/>,
/// so a sale SAP took after the read still comes off. A load landing on the warehouse
/// (<see cref="MarkWarehouseBatchSnapshotDue"/>) makes the next page refresh and wait up to
/// <c>SAP:WarehouseBatchSnapshotLoadWaitSeconds</c> for it; if SAP does not answer, the page shows the
/// figure from before the load, which is low, never high. The page's item codes come from the same read
/// (<see cref="WarehouseBatchSnapshot.StockedItemCodes"/>), so a stale page never lists a code without its
/// batches — the handset would fall back to the company-wide quantity for it.
/// </para>
/// </remarks>
public partial class SAPServiceLayerClient
{
    private const string WarehouseLoadLandedCacheKeyPrefix = "SAP_WarehouseLoadLanded_";

    /// <summary>How long a load-landed mark is remembered; far past any snapshot it could make due.</summary>
    private static readonly TimeSpan LoadLandedRetention = TimeSpan.FromDays(1);

    /// <summary>
    /// How long a background refresh is not tried again after the read family refused it with no slot
    /// due to free at a known time: the slots were taken by reads in flight, which end within their budget.
    /// </summary>
    private static readonly TimeSpan FamilyFullRetryAfter = TimeSpan.FromSeconds(30);

    private TimeSpan WarehouseBatchSnapshotFresh =>
        TimeSpan.FromMinutes(Math.Max(0, _settings.WarehouseBatchSnapshotFreshMinutes));

    private TimeSpan WarehouseBatchSnapshotMaxStale =>
        TimeSpan.FromMinutes(Math.Max(0, _settings.WarehouseBatchSnapshotMaxStaleMinutes));

    private TimeSpan WarehouseBatchSnapshotLoadWait =>
        TimeSpan.FromSeconds(Math.Max(0, _settings.WarehouseBatchSnapshotLoadWaitSeconds));

    /// <summary>How long an entry stays in memory: as long as anything may still read it, and a minute.</summary>
    private TimeSpan WarehouseBatchSnapshotRetention =>
        (WarehouseBatchSnapshotMaxStale > WarehouseBatchSnapshotLifetime
            ? WarehouseBatchSnapshotMaxStale
            : WarehouseBatchSnapshotLifetime) + TimeSpan.FromMinutes(1);

    /// <summary>
    /// Records that stock landed on <paramref name="warehouseCode"/> at <paramref name="landedAt"/>, so any
    /// batch read sent before then is refreshed before it answers another page as current.
    /// </summary>
    /// <remarks>
    /// A time rather than a flag on the entry, so a refresh already in flight when the load landed, which
    /// replaces the entry when it answers, does not take the mark with it.
    /// </remarks>
    internal static void MarkWarehouseBatchSnapshotDue(IMemoryCache cache, string? warehouseCode, DateTimeOffset landedAt)
    {
        if (string.IsNullOrWhiteSpace(warehouseCode))
        {
            return;
        }

        cache.Set(LoadLandedKey(warehouseCode), landedAt, LoadLandedRetention);
    }

    public async Task<WarehouseBatchSnapshot> GetWarehouseBatchesForDisplayAsync(
        string warehouseCode,
        CancellationToken cancellationToken = default)
    {
        var key = WarehouseBatchSnapshotKey(warehouseCode);
        var entry = PeekWarehouseBatchSnapshot(key);
        var now = _timeProvider.GetUtcNow();

        if (entry is not null)
        {
            var age = now - entry.ReadAt;
            var loadLanded = LoadLandedSince(warehouseCode, entry);

            if (!loadLanded && age < WarehouseBatchSnapshotFresh)
            {
                return entry.ToSnapshot(isStale: false);
            }

            if (age <= WarehouseBatchSnapshotMaxStale)
            {
                var refresh = RefreshWarehouseBatchSnapshotInBackground(key, warehouseCode, entry, now);

                if (loadLanded && refresh is not null && WarehouseBatchSnapshotLoadWait > TimeSpan.Zero)
                {
                    try
                    {
                        var fresher = await refresh.WaitAsync(WarehouseBatchSnapshotLoadWait, _timeProvider, cancellationToken);
                        return fresher.ToSnapshot(isStale: false);
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
                    {
                        // SAP did not answer in time or at all. The read from before the load stands in:
                        // it is low by what landed, never high.
                    }
                }

                _logger.LogInformation(
                    "Answered a catalogue page for {Warehouse} from its batch read of {ReadAt:HH:mm:ss} UTC, {AgeMinutes:0.0} min old{Reason}, while SAP is asked again",
                    warehouseCode,
                    entry.ReadAt,
                    age.TotalMinutes,
                    loadLanded ? ", from before a load landed" : string.Empty);

                return entry.ToSnapshot(isStale: true);
            }
        }

        // Nothing recent enough to stand in, so the page waits for SAP as it always did. Display-only: if
        // SAP is presumed to be still working on this very read, it is refused at once rather than becoming
        // a second stuck Service Layer process.
        var read = await WarehouseReadsInFlight.JoinAsync(
            key,
            () => ReadWarehouseBatchSnapshotAsync(key, warehouseCode),
            cancellationToken);

        return read.ToSnapshot(isStale: false);
    }

    /// <summary>
    /// The snapshot for <see cref="GetBatchNumbersForItemsInWarehouseAsync"/>'s display callers, which take
    /// their item codes from a separate two-minute read and so must not be handed an older batch read.
    /// </summary>
    private async Task<List<BatchNumber>> GetWarehouseBatchSnapshotAsync(
        string warehouseCode,
        CancellationToken cancellationToken)
    {
        var key = WarehouseBatchSnapshotKey(warehouseCode);
        var entry = PeekWarehouseBatchSnapshot(key);

        if (entry is not null
            && _timeProvider.GetUtcNow() - entry.ReadAt < WarehouseBatchSnapshotLifetime
            && !LoadLandedSince(warehouseCode, entry))
        {
            return entry.Batches;
        }

        var read = await WarehouseReadsInFlight.JoinAsync(
            key,
            () => ReadWarehouseBatchSnapshotAsync(key, warehouseCode),
            cancellationToken);

        return read.Batches;
    }

    /// <summary>
    /// Starts one refresh of <paramref name="warehouseCode"/>'s batch read, or joins the one running; null
    /// while SAP's pipeline has said a refresh would be refused.
    /// </summary>
    /// <remarks>
    /// Background priority whoever's page started it: the stuck-read hold then refuses a repeat of a read
    /// SAP is still working on, and the read family's background limit applies. No page waits on it except
    /// after a load (<see cref="WarehouseBatchSnapshotLoadWait"/>), and it runs on no page's token.
    /// </remarks>
    private Task<WarehouseBatchSnapshotEntry>? RefreshWarehouseBatchSnapshotInBackground(
        string key,
        string warehouseCode,
        WarehouseBatchSnapshotEntry entry,
        DateTimeOffset now)
    {
        if (!entry.MayRefreshAt(now))
        {
            return null;
        }

        Task<WarehouseBatchSnapshotEntry> refresh;
        using (SapRequestPriority.SuppressInteractive())
        {
            refresh = WarehouseReadsInFlight.JoinAsync(
                key,
                () => ReadWarehouseBatchSnapshotAsync(key, warehouseCode),
                CancellationToken.None);
        }

        _ = refresh.ContinueWith(
            failed => NoteFailedRefresh(warehouseCode, entry, failed.Exception!.GetBaseException()),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return refresh;
    }

    private void NoteFailedRefresh(string warehouseCode, WarehouseBatchSnapshotEntry entry, Exception error)
    {
        switch (error)
        {
            case SapReadStillRunningException held:
                entry.HoldRefreshUntil(held.HeldUntil);
                _logger.LogInformation(
                    "Not refreshing {Warehouse}'s batch read before {HeldUntil:HH:mm:ss} UTC: SAP is presumed to be still working on the last one. Its catalogue is answered from the read of {ReadAt:HH:mm:ss} UTC",
                    warehouseCode,
                    held.HeldUntil,
                    entry.ReadAt);
                break;

            case SapReadFamilyFullException full:
                var retryAt = full.FirstSlotFreesAt ?? _timeProvider.GetUtcNow() + FamilyFullRetryAfter;
                entry.HoldRefreshUntil(retryAt);
                _logger.LogInformation(
                    "Not refreshing {Warehouse}'s batch read before {RetryAt:HH:mm:ss} UTC: SAP already has all the {Family} reads it is let work on. Its catalogue is answered from the read of {ReadAt:HH:mm:ss} UTC",
                    warehouseCode,
                    retryAt,
                    full.Family,
                    entry.ReadAt);
                break;

            default:
                _logger.LogWarning(
                    "Refreshing {Warehouse}'s batch read failed ({Error}); its catalogue is answered from the read of {ReadAt:HH:mm:ss} UTC until a refresh answers",
                    warehouseCode,
                    error.Message,
                    entry.ReadAt);
                break;
        }
    }

    /// <summary>
    /// Reads the warehouse's batches whole and keeps them as its snapshot, timed from when the read was sent.
    /// </summary>
    /// <remarks>
    /// Runs on no caller's token: it is shared by every page that joins it, and bounds itself by the stock
    /// budget. A failure is not kept, so the last good read keeps standing in.
    /// </remarks>
    private async Task<WarehouseBatchSnapshotEntry> ReadWarehouseBatchSnapshotAsync(string key, string warehouseCode)
    {
        var sentAt = _timeProvider.GetUtcNow();
        var batches = await ReadAllBatchNumbersInWarehouseAsync(warehouseCode, displayOnly: true, CancellationToken.None);
        var entry = new WarehouseBatchSnapshotEntry(batches, sentAt);
        _memoryCache.Set(key, entry, WarehouseBatchSnapshotRetention);
        return entry;
    }

    private WarehouseBatchSnapshotEntry? PeekWarehouseBatchSnapshot(string key) =>
        _memoryCache.TryGetValue(key, out WarehouseBatchSnapshotEntry? entry) ? entry : null;

    /// <summary>Whether a whole batch read of <paramref name="warehouseCode"/> is on its way to SAP, for tests.</summary>
    internal static bool IsWarehouseBatchReadInFlight(string warehouseCode) =>
        WarehouseReadsInFlight.IsInFlight(WarehouseBatchSnapshotKey(warehouseCode));

    /// <summary>When a load last landed on <paramref name="warehouseCode"/>, if one has within the day.</summary>
    internal static DateTimeOffset? WarehouseLoadLandedAt(IMemoryCache cache, string warehouseCode) =>
        cache.TryGetValue(LoadLandedKey(warehouseCode), out DateTimeOffset landedAt) ? landedAt : null;

    private bool LoadLandedSince(string warehouseCode, WarehouseBatchSnapshotEntry entry) =>
        WarehouseLoadLandedAt(_memoryCache, warehouseCode) is { } landedAt && landedAt >= entry.ReadAt;

    private static string WarehouseBatchSnapshotKey(string warehouseCode) =>
        WarehouseBatchSnapshotCacheKeyPrefix + warehouseCode.Trim().ToUpperInvariant();

    private static string LoadLandedKey(string warehouseCode) =>
        WarehouseLoadLandedCacheKeyPrefix + warehouseCode.Trim().ToUpperInvariant();

    /// <summary>One warehouse's last good whole batch read, and when it was sent.</summary>
    internal sealed class WarehouseBatchSnapshotEntry(List<BatchNumber> batches, DateTimeOffset readAt)
    {
        private long _refreshNotBeforeUtcTicks;

        public List<BatchNumber> Batches { get; } = batches;

        public DateTimeOffset ReadAt { get; } = readAt;

        /// <summary>
        /// The codes holding batch stock, distinct and in the order <see cref="WarehouseItemCursor"/>
        /// searches. The read is the same <c>OBTN ⋈ OBTQ</c> rows, <c>Quantity &gt; 0</c>, that the
        /// warehouse item-code read selects from, so the two lists hold the same codes.
        /// </summary>
        public IReadOnlyList<string> StockedItemCodes { get; } = batches
            .Select(batch => batch.ItemCode)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        public bool MayRefreshAt(DateTimeOffset now) => now.UtcTicks >= Interlocked.Read(ref _refreshNotBeforeUtcTicks);

        public void HoldRefreshUntil(DateTimeOffset until) => Interlocked.Exchange(ref _refreshNotBeforeUtcTicks, until.UtcTicks);

        public WarehouseBatchSnapshot ToSnapshot(bool isStale) => new(Batches, ReadAt, isStale, StockedItemCodes);
    }
}

/// <summary>
/// One warehouse's batches as SAP last gave them whole, for a catalogue page.
/// </summary>
/// <param name="Batches">Every batch with stock in the warehouse.</param>
/// <param name="ReadAtUtc">When the read that produced them was sent to SAP.</param>
/// <param name="IsStale">
/// True when the read is older than <c>SAP:WarehouseBatchSnapshotFreshMinutes</c>, or a load landed after
/// it, and SAP is being asked again in the background.
/// </param>
/// <param name="StockedItemCodes">
/// The codes holding batch stock in <paramref name="Batches"/>, distinct, in cursor order. A page takes its
/// codes from here so that its codes and its batches come from the same read.
/// </param>
public sealed record WarehouseBatchSnapshot(
    IReadOnlyList<BatchNumber> Batches,
    DateTimeOffset ReadAtUtc,
    bool IsStale,
    IReadOnlyList<string> StockedItemCodes);
