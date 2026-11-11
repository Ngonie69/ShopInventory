using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;

namespace ShopInventory.Services;

/// <summary>
/// The SQLQueries reads SAP took, did not answer, and is presumed to be still working on, so that
/// background work does not hand the Service Layer the same read again meanwhile.
/// </summary>
/// <remarks>
/// <para>
/// Measured on KFLDB over 21 hours on 2026-11-10/11, by joining the Service Layer's access logs to
/// 15-second samples of its processes' memory. A Service Layer process that gets stuck on one of
/// these reads keeps working on it at full CPU for minutes to two hours, its memory cycling up to
/// about 8 GB, long after this client gave up and after the load balancer answered 502 at its 300 s
/// ProxyTimeout. Nothing this client does stops it: the request is gone, the work is not. Ten such
/// processes held 56 GB at 14:08 on 2026-11-10 and left 13.8 GB between HANA and the kernel's
/// out-of-memory killer, which had killed hdbindexserver 106 times since 2026-09-28.
/// </para>
/// <para>
/// 210 of the 226 stuck processes were serving a read from this API, nearly all
/// <c>WHS_BATCHES_*</c>, <c>STK_ITEMS_*</c> or <c>DSK_INV_COST_RANGE</c>. Of 300 reads that took
/// over a minute, 159 were asked for again with the identical URL within 15 minutes: 130 repeats went
/// out while the first was still running, and 110 repeats got stuck too.
/// </para>
/// <para>
/// In memory and per process, like the circuit breaker: a recycle forgets the holds, which costs at
/// most one more stuck read per URL.
/// </para>
/// </remarks>
public sealed class SapStuckReadState(IOptions<SAPSettings> settings, TimeProvider timeProvider)
{
    /// <summary>How long SAP must have had a read, without answering, before it is presumed stuck.</summary>
    /// <remarks>
    /// A healthy read of these shapes answers in well under a second. A caller that gives up sooner — a
    /// closed page — says nothing about SAP. The shortest budget that cuts a read off on purpose is the
    /// price lists' 20 seconds.
    /// </remarks>
    public static readonly TimeSpan MinimumTimeAtSap = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Expired holds are swept once there are this many. Far above anything seen: a stuck read is a
    /// few per minute at the worst hour, and each hold lasts minutes.
    /// </summary>
    private const int SweepAbove = 1000;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _holds = new(StringComparer.Ordinal);

    /// <summary>How long a hold lasts; zero when the guard is turned off.</summary>
    public TimeSpan HoldDuration => TimeSpan.FromMinutes(Math.Max(0, settings.Value.StuckReadHoldMinutes));

    public bool Enabled => HoldDuration > TimeSpan.Zero;

    public DateTimeOffset UtcNow => timeProvider.GetUtcNow();

    /// <summary>Whether the read named by <paramref name="key"/> is held, and until when.</summary>
    public bool IsHeld(string key, out DateTimeOffset until)
    {
        if (_holds.TryGetValue(key, out until))
        {
            if (until > timeProvider.GetUtcNow())
            {
                return true;
            }

            _holds.TryRemove(new KeyValuePair<string, DateTimeOffset>(key, until));
        }

        until = default;
        return false;
    }

    /// <summary>Holds the read named by <paramref name="key"/> from now; returns when the hold ends.</summary>
    public DateTimeOffset Hold(string key)
    {
        var now = timeProvider.GetUtcNow();
        if (_holds.Count >= SweepAbove)
        {
            foreach (var hold in _holds)
            {
                if (hold.Value <= now)
                {
                    _holds.TryRemove(hold);
                }
            }
        }

        var until = now + HoldDuration;
        _holds[key] = until;
        return until;
    }

    /// <summary>Ends the hold on <paramref name="key"/>, if there is one: SAP answered that read.</summary>
    public void Release(string key) => _holds.TryRemove(key, out _);
}
