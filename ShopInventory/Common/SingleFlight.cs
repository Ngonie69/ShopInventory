using System.Collections.Concurrent;

namespace ShopInventory.Common;

/// <summary>
/// At most one load in flight per key: callers that arrive while one is running wait for it and
/// get its result, instead of each starting the same read.
/// </summary>
/// <remarks>
/// <para>
/// Made for a cache entry that has just expired. Without it, every caller that finds the entry
/// missing starts its own load, and for a whole-warehouse SAP read that is several copies of the
/// query most likely to hang, each holding one of the process's few SAP slots.
/// </para>
/// <para>
/// The load runs on no caller's token, so one caller giving up does not fail the others; each caller
/// stops waiting on its own token. The load must therefore bound itself, as the SAP stock budget
/// does. Nothing is kept once the load ends: success is the cache's to hold, and a failure is
/// not remembered, so the next caller tries again.
/// </para>
/// </remarks>
public sealed class SingleFlight
{
    private readonly ConcurrentDictionary<string, Lazy<Task<object?>>> _inFlight = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns the result of the load running under <paramref name="key"/>, starting
    /// <paramref name="load"/> when none is.
    /// </summary>
    public async Task<T> JoinAsync<T>(string key, Func<Task<T>> load, CancellationToken cancellationToken)
    {
        Lazy<Task<object?>> flight = null!;
        flight = new Lazy<Task<object?>>(async () =>
        {
            try
            {
                return await load();
            }
            finally
            {
                // Only this flight: a later one under the same key must not be dropped by this one ending.
                _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<object?>>>(key, flight));
            }
        });

        var current = _inFlight.GetOrAdd(key, flight);
        return (T)(await current.Value.WaitAsync(cancellationToken))!;
    }

    /// <summary>How many loads are running, for tests.</summary>
    internal int Count => _inFlight.Count;
}
