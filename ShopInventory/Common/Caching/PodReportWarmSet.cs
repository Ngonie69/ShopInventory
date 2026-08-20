using System.Collections.Concurrent;

namespace ShopInventory.Common.Caching;

/// <summary>One POD report shape somebody has recently asked for.</summary>
public readonly record struct PodReportWarmKey(DateTime FromDate, DateTime ToDate, string ScopeKey);

/// <summary>
/// Remembers which POD report shapes are in active use, so they can be rebuilt off the request path
/// before they go stale.
/// </summary>
/// <remarks>
/// The report is cached, and the cache works — a hit answers in 30–90 ms. What hurt on 2026-08-20
/// was the misses: six cold builds in ninety minutes, between 6 and 64 seconds each, every one of
/// them paid for by a person sitting in front of the screen. The cache is keyed on the exact date
/// range, users pick their own, and the entries live fifteen minutes, so an ordinary morning's use
/// straddles expiry over and over.
/// <para>
/// Warming a fixed list of ranges on a timer would be worse than the disease: four preset ranges
/// refreshed every ten minutes is several hundred rebuilds a day against SAP, most of them for
/// reports nobody opened. Recording what was actually asked for makes the warming follow real use —
/// nothing at all overnight, and during the working day only the handful of shapes in play. It does
/// not reduce the SAP work much; it moves it off the request path, which is the part a person feels.
/// </para>
/// <para>
/// Deliberately in memory rather than a column on the cache table. Losing the set on a restart costs
/// exactly one cold build per shape to relearn it, which is what would have happened anyway, and it
/// keeps a report read from writing to the database.
/// </para>
/// </remarks>
public sealed class PodReportWarmSet
{
    /// <summary>How long after its last request a shape is still worth keeping warm.</summary>
    public static readonly TimeSpan ActiveWindow = TimeSpan.FromHours(1);

    /// <summary>
    /// A ceiling on how many shapes are tracked, so an unusual run of one-off custom ranges cannot
    /// turn the warm job into a rebuild storm. Well above the two or three a normal day uses.
    /// </summary>
    private const int MaxTrackedShapes = 12;

    private readonly ConcurrentDictionary<PodReportWarmKey, DateTime> _lastRequestedUtc = new();

    /// <summary>Notes that somebody asked for this shape.</summary>
    public void Record(PodReportWarmKey key)
    {
        var now = DateTime.UtcNow;
        _lastRequestedUtc[key] = now;

        if (_lastRequestedUtc.Count <= MaxTrackedShapes)
        {
            return;
        }

        // Drop the coldest first: an active shape is one somebody is coming back to.
        foreach (var stale in _lastRequestedUtc
            .OrderBy(entry => entry.Value)
            .Take(_lastRequestedUtc.Count - MaxTrackedShapes)
            .Select(entry => entry.Key))
        {
            _lastRequestedUtc.TryRemove(stale, out _);
        }
    }

    /// <summary>The shapes asked for inside <see cref="ActiveWindow"/>, newest request first.</summary>
    public IReadOnlyList<PodReportWarmKey> ActiveShapes()
    {
        var cutoff = DateTime.UtcNow - ActiveWindow;

        return _lastRequestedUtc
            .Where(entry => entry.Value >= cutoff)
            .OrderByDescending(entry => entry.Value)
            .Select(entry => entry.Key)
            .ToList();
    }
}
