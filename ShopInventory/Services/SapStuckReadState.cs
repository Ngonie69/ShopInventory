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
/// What a stuck process is doing was found on 2026-11-22: writing one decimal of the answer with a
/// precision of about two billion digits, over and over (<see cref="ShopInventory.Common.Sap.SapSqlAmount"/>).
/// It never finishes; only killing it ends it. So a hold that simply runs out sends the read to its
/// next stuck process. Overnight on 2026-11-22/23 the invoice cost copy ran every 15 minutes against a
/// 10-minute hold, sent the same two <c>DSK_INV_COST_RANGE</c> reads each time, and each send left one
/// more process stuck: 13 of them held 76.8 GB at 04:45 and HANA's index server was killed six times.
/// </para>
/// <para>
/// So the hold grows. The first lasts <see cref="HoldDuration"/>; each time the same read gets stuck
/// again without SAP having answered it in between, the next hold is four times as long, up to
/// <see cref="MaxHoldDuration"/>: 10 minutes, 40, 160, 640, then a day. One slow read on a bad
/// afternoon still costs ten minutes; a read that sticks every time is sent four times in the first
/// fourteen hours rather than fifty-six. SAP answering the read ends it and starts the count again.
/// </para>
/// <para>
/// In memory and per process, like the circuit breaker: a recycle forgets the holds, which costs at
/// most one more stuck read per URL. Work that must not pay even that keeps its own record, as the
/// invoice cost copy does (<see cref="SaleInvoiceCostSyncService"/>).
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

    /// <summary>How much longer each hold on the same read is than the one before it.</summary>
    internal const int HoldGrowth = 4;

    /// <summary>
    /// Finished holds are swept once there are this many. Far above anything seen: a stuck read is a
    /// few per minute at the worst hour.
    /// </summary>
    private const int SweepAbove = 1000;

    private readonly ConcurrentDictionary<string, StuckRead> _holds = new(StringComparer.Ordinal);

    /// <summary>A read's hold: when it ends, and how many times running the read has got stuck.</summary>
    private readonly record struct StuckRead(DateTimeOffset Until, int Times);

    /// <summary>How long the first hold on a read lasts; zero when the guard is turned off.</summary>
    public TimeSpan HoldDuration => TimeSpan.FromMinutes(Math.Max(0, settings.Value.StuckReadHoldMinutes));

    /// <summary>The longest a hold grows to. Never shorter than <see cref="HoldDuration"/>.</summary>
    public TimeSpan MaxHoldDuration
    {
        get
        {
            var longest = TimeSpan.FromMinutes(Math.Max(0, settings.Value.StuckReadMaxHoldMinutes));
            return longest > HoldDuration ? longest : HoldDuration;
        }
    }

    public bool Enabled => HoldDuration > TimeSpan.Zero;

    public DateTimeOffset UtcNow => timeProvider.GetUtcNow();

    /// <summary>The key a read is held under: its method and whole URL, so another page is another read.</summary>
    public static string KeyOf(HttpRequestMessage request) => $"{request.Method} {request.RequestUri!.AbsoluteUri}";

    /// <summary>
    /// How long SAP had <paramref name="request"/>, when that is long enough to presume SAP is still
    /// working on it after it ended without an answer; otherwise null.
    /// </summary>
    /// <remarks>
    /// Measured from <see cref="SapRequestMarks.MarkReachedSap"/>, so time spent queueing for a slot does
    /// not count: a read that never reached SAP leaves nothing running there.
    /// </remarks>
    public TimeSpan? PresumedStuckAfter(HttpRequestMessage request)
    {
        if (!SapRequestMarks.TryGetReachedSapAt(request, out var reachedAt))
        {
            return null;
        }

        var atSap = UtcNow - reachedAt;
        return atSap >= MinimumTimeAtSap ? atSap : null;
    }

    /// <summary>Whether the read named by <paramref name="key"/> is held, and until when.</summary>
    /// <remarks>
    /// A hold that has run out stays on record until SAP answers the read (<see cref="Release"/>): it is
    /// what makes the next one longer if the read gets stuck again.
    /// </remarks>
    public bool IsHeld(string key, out DateTimeOffset until)
    {
        if (_holds.TryGetValue(key, out var hold) && hold.Until > timeProvider.GetUtcNow())
        {
            until = hold.Until;
            return true;
        }

        until = default;
        return false;
    }

    /// <summary>
    /// Holds the read named by <paramref name="key"/> from now; returns when the hold ends. Longer each
    /// time the read gets stuck again before SAP has answered it.
    /// </summary>
    public DateTimeOffset Hold(string key)
    {
        var now = timeProvider.GetUtcNow();
        if (_holds.Count >= SweepAbove)
        {
            // Long finished, so nothing is being asked for it any more and its count can go.
            var forgotten = now - MaxHoldDuration;
            foreach (var hold in _holds)
            {
                if (hold.Value.Until <= forgotten)
                {
                    _holds.TryRemove(hold);
                }
            }
        }

        return _holds.AddOrUpdate(
            key,
            _ => new StuckRead(now + HoldAfter(1), 1),
            (_, earlier) => new StuckRead(now + HoldAfter(earlier.Times + 1), earlier.Times + 1)).Until;
    }

    /// <summary>How long the hold lasts once a read has got stuck <paramref name="times"/> times running.</summary>
    internal TimeSpan HoldAfter(int times)
    {
        var hold = HoldDuration;
        var longest = MaxHoldDuration;
        for (var repeat = 1; repeat < times && hold < longest; repeat++)
        {
            hold *= HoldGrowth;
        }

        return hold < longest ? hold : longest;
    }

    /// <summary>
    /// Ends the hold on <paramref name="key"/>, if there is one, and forgets how often the read got
    /// stuck: SAP answered it.
    /// </summary>
    public void Release(string key) => _holds.TryRemove(key, out _);
}
