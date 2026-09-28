using System.Collections.Concurrent;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Common;

/// <summary>
/// Dashboard figures shared by every open dashboard for a short while, instead of each one reading them.
/// </summary>
/// <remarks>
/// Each dashboard view read its own figures: today's invoice value by walking up to ten pages of invoice
/// headers from SAP one after another, the day's payments twice, and the day's audit log 500 rows at a
/// time, just to show a few numbers. Twenty people with a dashboard open were twenty of each. Now one
/// load serves every dashboard for the figure's lifetime, and callers that arrive while it is under way
/// wait for that load rather than starting their own.
///
/// Only a load that answered is kept. A read that failed returns null, is not stored, and the next caller
/// tries again.
///
/// Only for figures that do not depend on who is asking. The ones here are whole-company counts and totals
/// that the dashboards' own roles may all read.
/// </remarks>
public static class SharedFigures
{
    /// <summary>How long a figure for today is shared: long enough to absorb a crowd, short enough to read as live.</summary>
    public static readonly TimeSpan Today = TimeSpan.FromSeconds(60);

    /// <summary>How long a figure for an earlier day is shared. It moves only with late postings.</summary>
    public static readonly TimeSpan EarlierDay = TimeSpan.FromMinutes(10);

    private const int PruneAbove = 64;

    private static readonly ConcurrentDictionary<string, Entry> Entries = new(StringComparer.Ordinal);

    /// <summary>The clock expiry is read from; a test replaces it.</summary>
    internal static TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>The lifetime for a figure about <paramref name="date"/>.</summary>
    public static TimeSpan LifetimeFor(DateTime date) => date.Date >= DateTime.Today ? Today : EarlierDay;

    /// <summary>
    /// The shared value for <paramref name="key"/>, loading it with <paramref name="load"/> when there is
    /// none still fresh. Null when the load failed or answered nothing.
    /// </summary>
    public static async Task<T?> GetAsync<T>(string key, TimeSpan lifetime, Func<Task<T?>> load)
        where T : class
    {
        var now = Clock.GetUtcNow();
        var entry = Entries.AddOrUpdate(
            key,
            _ => new Entry(Start(load), now + lifetime),
            (_, existing) => existing.IsUsable(now) ? existing : new Entry(Start(load), now + lifetime));

        var value = await entry.Load.Value as T;
        if (value is null)
        {
            // Not kept: the next caller asks again.
            Entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
        }

        if (Entries.Count > PruneAbove)
        {
            foreach (var stale in Entries.Where(pair =>
                         pair.Value.Load.IsValueCreated && pair.Value.Load.Value.IsCompleted && pair.Value.Expires <= now))
                Entries.TryRemove(stale);
        }

        return value;
    }

    internal static void ResetForTests() => Entries.Clear();

    /// <summary>Lazy, so an entry that loses a race in AddOrUpdate is never started.</summary>
    private static Lazy<Task<object?>> Start<T>(Func<Task<T?>> load) where T : class =>
        new(async () =>
        {
            try
            {
                // Everyone waiting on this figure shares the load; whoever started it leaving the page
                // must not cancel it for the rest (see PageReads).
                using var sharedLoad = PageReads.Detach();
                return await load();
            }
            catch
            {
                return null;
            }
        });

    private sealed class Entry(Lazy<Task<object?>> load, DateTimeOffset expires)
    {
        public Lazy<Task<object?>> Load { get; } = load;
        public DateTimeOffset Expires { get; } = expires;

        /// <summary>Still loading, or loaded, answered and not yet expired.</summary>
        public bool IsUsable(DateTimeOffset now) =>
            !Load.IsValueCreated
            || !Load.Value.IsCompleted
            || (Expires > now && Load.Value.Result is not null);
    }
}
