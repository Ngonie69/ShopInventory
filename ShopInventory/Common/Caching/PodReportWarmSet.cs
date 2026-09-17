using System.Collections.Concurrent;

namespace ShopInventory.Common.Caching;

/// <summary>One POD report shape somebody has recently asked for.</summary>
public readonly record struct PodReportWarmKey(DateTime FromDate, DateTime ToDate, string ScopeKey);

/// <summary>A tracked shape, with the shops a scoped report was built for.</summary>
/// <param name="Key">The range and cache scope asked for.</param>
/// <param name="CustomerCodes">
/// The shops behind a scoped <see cref="PodReportWarmKey.ScopeKey"/>, which is a one-way hash of them
/// and cannot be turned back into the list. Null for the global report.
/// </param>
public sealed record PodReportWarmShape(PodReportWarmKey Key, IReadOnlyList<string>? CustomerCodes);

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

    private readonly ConcurrentDictionary<PodReportWarmKey, Entry> _entries = new();
    private readonly TimeProvider _timeProvider;

    public PodReportWarmSet(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Notes that somebody asked for this shape.</summary>
    /// <param name="key">The shape asked for.</param>
    /// <param name="customerCodes">
    /// The shops a scoped report was built for, or null for the global report. Kept because the scope
    /// key is a hash: without the shops the job cannot rebuild the report the key names.
    /// </param>
    public void Record(PodReportWarmKey key, IReadOnlyCollection<string>? customerCodes = null)
    {
        // A copy, so a caller reusing its collection cannot change what a tracked key rebuilds.
        var codes = customerCodes?.ToArray();
        _entries[key] = new Entry(_timeProvider.GetUtcNow().UtcDateTime, codes);

        if (_entries.Count <= MaxTrackedShapes)
        {
            return;
        }

        // Drop the coldest first: an active shape is one somebody is coming back to.
        foreach (var stale in _entries
            .OrderBy(entry => entry.Value.LastRequestedUtc)
            .Take(_entries.Count - MaxTrackedShapes)
            .Select(entry => entry.Key))
        {
            _entries.TryRemove(stale, out _);
        }
    }

    /// <summary>The shapes asked for inside <see cref="ActiveWindow"/>, newest request first.</summary>
    public IReadOnlyList<PodReportWarmShape> ActiveShapes()
    {
        var cutoff = _timeProvider.GetUtcNow().UtcDateTime - ActiveWindow;

        return _entries
            .Where(entry => entry.Value.LastRequestedUtc >= cutoff)
            .OrderByDescending(entry => entry.Value.LastRequestedUtc)
            .Select(entry => new PodReportWarmShape(entry.Key, entry.Value.CustomerCodes))
            .ToList();
    }

    private sealed record Entry(DateTime LastRequestedUtc, IReadOnlyList<string>? CustomerCodes);
}
