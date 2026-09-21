namespace ShopInventory.Services.Telematics;

/// <summary>
/// Counts the Cartrack requests one job run makes, and stops it at an allowance.
/// </summary>
/// <remarks>
/// <para>
/// Scoped, so it is one job run's count: the typed <see cref="CartrackClient"/> is resolved from
/// the same scope as the service driving it and takes a request from here before every attempt,
/// retries included — a retry is a request as far as the provider's limits are concerned.
/// </para>
/// <para>
/// The per-minute ceilings in <see cref="CartrackRateLimiter"/> pace requests; this caps how many
/// there are. The daily total it serves is persisted by the rollup in its checkpoint, so it holds
/// across restarts and across cluster nodes, which an in-memory counter would not.
/// </para>
/// <para>
/// Exhaustion is a <see cref="CartrackRateLimitedException"/> on purpose. Every read in the rollup
/// already lets that one exception through its per-facet catches, so a spent budget stops the pass
/// cleanly instead of being logged as a failed odometer on every remaining van.
/// </para>
/// </remarks>
public sealed class CartrackRequestBudget
{
    private long _used;
    private long? _allowance;

    /// <summary>Requests made through this budget so far.</summary>
    public long Used => Interlocked.Read(ref _used);

    /// <summary>Requests still allowed, or null when nothing caps this run.</summary>
    public long? Remaining => _allowance is { } cap ? Math.Max(0, cap - Used) : null;

    /// <summary>Whether a request has been refused for want of budget.</summary>
    public bool Exhausted { get; private set; }

    /// <summary>Caps the run at <paramref name="requests"/> more requests; null lifts the cap.</summary>
    public void Allow(long? requests) => _allowance = requests is { } cap ? Used + Math.Max(0, cap) : null;

    /// <summary>Takes one request, or refuses it once the allowance is spent.</summary>
    public void Take(string path)
    {
        if (_allowance is { } cap && Used >= cap)
        {
            Exhausted = true;

            var now = DateTime.UtcNow;
            var tomorrow = CartrackTime.UtcWindowOf(CartrackTime.TradingDateOf(now).AddDays(1)).FromUtc;

            throw new CartrackRateLimitedException(tomorrow - now, $"{path} (daily request budget)");
        }

        Interlocked.Increment(ref _used);
    }
}
