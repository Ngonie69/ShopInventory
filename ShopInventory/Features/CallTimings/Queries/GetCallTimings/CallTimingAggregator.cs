using ShopInventory.Services.CallTimings;

namespace ShopInventory.Features.CallTimings.Queries.GetCallTimings;

/// <summary>
/// The arithmetic behind the call timings page: per-family and per-route percentiles, the slowest SAP
/// calls, slot waits, and the stage of each call in flight.
/// </summary>
/// <remarks>
/// <para>
/// Pure: everything comes in on a <see cref="CallTimingSnapshot"/>, so the tests drive it with records
/// they wrote rather than with a running pipeline.
/// </para>
/// <para>
/// Percentiles are nearest-rank over the exact durations, not a histogram's estimate: the ring keeps
/// every call, and sorting a day's worth per family takes milliseconds. SAP percentiles are of the time
/// SAP had each call, over the calls that reached it, so a refused read or a long queue does not pass for
/// SAP being slow; the queueing has its own figures.
/// </para>
/// </remarks>
public static class CallTimingAggregator
{
    public const int SlowestCount = 25;
    public const int RouteCount = 100;
    public const int TopCallersPerRoute = 3;

    /// <summary>A wait at least this long counts as having waited for a slot.</summary>
    public const int WaitedThresholdMs = 100;

    /// <summary>The threshold <c>SAPConcurrencyHandler</c> logs a wait at.</summary>
    public const int LongWaitThresholdMs = 2000;

    public static GetCallTimingsResult Aggregate(CallTimingSnapshot snapshot)
    {
        var windowStartTicks = snapshot.WindowStartUtc.Ticks;
        var sapCalls = snapshot.SapCalls.Where(call => call.StartedUtcTicks >= windowStartTicks).ToList();
        var inbound = snapshot.InboundCalls.Where(call => call.StartedUtcTicks >= windowStartTicks).ToList();
        var limited = snapshot.Slots.Families.Select(family => family.Family).ToHashSet(StringComparer.Ordinal);

        return new GetCallTimingsResult
        {
            Enabled = true,
            Window = snapshot.Window,
            GeneratedAtUtc = snapshot.NowUtc,
            WindowStartUtc = snapshot.WindowStartUtc,
            RecordingSinceUtc = snapshot.RecordingSinceUtc,
            SapCoverageStartUtc = CoverageStart(snapshot, snapshot.SapDropped, snapshot.SapOldestKeptUtc),
            InboundCoverageStartUtc = CoverageStart(snapshot, snapshot.InboundDropped, snapshot.InboundOldestKeptUtc),
            SapCalls = sapCalls.Count,
            InboundCalls = inbound.Count,
            Slots = snapshot.Slots,
            SlotWaits = SlotWaits(sapCalls),
            Families = Families(sapCalls, snapshot.InFlight, limited),
            Slowest = Slowest(sapCalls),
            InFlight = [.. snapshot.InFlight.OrderByDescending(call => call.AgeMs)],
            Routes = Routes(inbound),
            Callers = Callers(inbound),
        };
    }

    /// <summary>
    /// A call in the pipeline as the page shows it. Its stage is <c>family-slot</c> while it queues for a
    /// limited read family, <c>slot</c> while it queues for a process-wide slot, then <c>at-sap</c>.
    /// </summary>
    public static SapCallInFlightTiming InFlightRow(SapCallInFlight call, TimeProvider clock, bool familyLimited)
    {
        var now = clock.GetTimestamp();
        var reached = call.ReachedSapTimestamp;
        var stage = reached != 0 ? "at-sap"
            : familyLimited && call.FamilyWait is null ? "family-slot"
            : "slot";

        return new SapCallInFlightTiming(
            call.StartedUtc,
            Milliseconds(clock.GetElapsedTime(call.StartedTimestamp, now)),
            call.Family,
            call.Operation,
            stage,
            reached == 0 ? 0 : Milliseconds(clock.GetElapsedTime(reached, now)),
            call.Interactive,
            call.Origin);
    }

    /// <summary>The nearest-rank percentile of an ascending list; 0 for an empty one.</summary>
    public static int Percentile(IReadOnlyList<int> ascending, double fraction)
    {
        if (ascending.Count == 0)
        {
            return 0;
        }

        var rank = (int)Math.Ceiling(fraction * ascending.Count);
        return ascending[Math.Clamp(rank - 1, 0, ascending.Count - 1)];
    }

    /// <summary>Whether a call got as far as SAP: it was answered, or SAP had it for some time.</summary>
    public static bool ReachedSap(in SapCallRecord call) =>
        call.Outcome != SapCallOutcome.NotSent && (call.StatusCode != 0 || call.AtSapMs > 0);

    public static string OutcomeName(SapCallOutcome outcome) => outcome switch
    {
        SapCallOutcome.Ok => "ok",
        SapCallOutcome.Error => "error",
        SapCallOutcome.Timeout => "timeout",
        SapCallOutcome.GaveUp => "gave-up",
        SapCallOutcome.NotSent => "not-sent",
        _ => outcome.ToString().ToLowerInvariant(),
    };

    private static DateTime CoverageStart(CallTimingSnapshot snapshot, bool dropped, DateTime? oldestKept)
    {
        var start = snapshot.WindowStartUtc > snapshot.RecordingSinceUtc ? snapshot.WindowStartUtc : snapshot.RecordingSinceUtc;
        return dropped && oldestKept is { } oldest && oldest > start ? oldest : start;
    }

    private static SapSlotWaitSummary SlotWaits(List<SapCallRecord> calls)
    {
        var waits = calls.Select(call => call.FamilyWaitMs + call.SlotWaitMs).Order().ToList();
        return new SapSlotWaitSummary(
            calls.Count,
            waits.Count(wait => wait >= WaitedThresholdMs),
            waits.Count(wait => wait >= LongWaitThresholdMs),
            Percentile(waits, 0.95),
            waits.Count == 0 ? 0 : waits[^1],
            calls.Count == 0 ? 0 : calls.Max(call => call.FamilyWaitMs),
            calls.Count == 0 ? 0 : calls.Max(call => call.SlotWaitMs),
            Seconds(waits.Sum(wait => (long)wait)),
            [PriorityWait("interactive", calls.Where(call => call.Interactive)),
             PriorityWait("background", calls.Where(call => !call.Interactive))]);
    }

    private static SapPriorityWait PriorityWait(string priority, IEnumerable<SapCallRecord> calls)
    {
        var waits = calls.Select(call => call.FamilyWaitMs + call.SlotWaitMs).Order().ToList();
        return new SapPriorityWait(
            priority,
            waits.Count,
            waits.Count(wait => wait >= WaitedThresholdMs),
            Percentile(waits, 0.95),
            waits.Count == 0 ? 0 : waits[^1]);
    }

    private static List<SapFamilyTiming> Families(
        List<SapCallRecord> calls,
        IReadOnlyList<SapCallInFlightTiming> inFlight,
        HashSet<string> limited)
    {
        var inFlightByFamily = inFlight.GroupBy(call => call.Family, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var families = calls
            .GroupBy(call => call.Family, StringComparer.Ordinal)
            .Select(group =>
            {
                var atSap = group.Where(call => ReachedSap(call)).Select(call => call.AtSapMs).Order().ToList();
                var waits = group.Select(call => call.FamilyWaitMs + call.SlotWaitMs).Order().ToList();
                return new SapFamilyTiming(
                    group.Key,
                    limited.Contains(group.Key),
                    group.Count(),
                    group.Count(call => call.Interactive),
                    group.Count(call => call.Outcome == SapCallOutcome.Ok),
                    group.Count(call => call.Outcome == SapCallOutcome.Error),
                    group.Count(call => call.Outcome == SapCallOutcome.Timeout),
                    group.Count(call => call.Outcome == SapCallOutcome.GaveUp),
                    group.Count(call => call.Outcome == SapCallOutcome.NotSent),
                    group.Count(call => call.PresumedStuck),
                    Percentile(atSap, 0.50),
                    Percentile(atSap, 0.95),
                    atSap.Count == 0 ? 0 : atSap[^1],
                    Seconds(atSap.Sum(value => (long)value)),
                    waits.Count(wait => wait >= WaitedThresholdMs),
                    Percentile(waits, 0.95),
                    waits.Count == 0 ? 0 : waits[^1],
                    inFlightByFamily.GetValueOrDefault(group.Key));
            })
            .ToList();

        // A family with calls in flight and none finished in the window is the one most worth seeing.
        foreach (var (family, count) in inFlightByFamily)
        {
            if (families.All(row => row.Family != family))
            {
                families.Add(new SapFamilyTiming(
                    family, limited.Contains(family), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, count));
            }
        }

        // Where SAP spent its time first: that is where a stuck read shows.
        return [.. families
            .OrderByDescending(row => row.TotalSeconds)
            .ThenByDescending(row => row.InFlightNow)
            .ThenByDescending(row => row.Calls)
            .ThenBy(row => row.Family, StringComparer.Ordinal)];
    }

    private static List<SapCallTiming> Slowest(List<SapCallRecord> calls) =>
    [
        .. calls
            .OrderByDescending(call => call.DurationMs)
            .ThenByDescending(call => call.StartedUtcTicks)
            .Take(SlowestCount)
            .Select(call => new SapCallTiming(
                new DateTime(call.StartedUtcTicks, DateTimeKind.Utc),
                call.Family,
                call.Operation,
                call.DurationMs,
                call.AtSapMs,
                call.FamilyWaitMs,
                call.SlotWaitMs,
                OutcomeName(call.Outcome),
                call.StatusCode,
                call.Interactive,
                call.PresumedStuck,
                call.Origin))
    ];

    private static List<InboundRouteTiming> Routes(List<InboundCallRecord> calls) =>
    [
        .. calls
            .GroupBy(call => (call.Method, call.Route))
            .Select(group =>
            {
                var durations = group.Select(call => call.DurationMs).Order().ToList();
                return new InboundRouteTiming(
                    group.Key.Method,
                    group.Key.Route,
                    durations.Count,
                    group.Count(call => call.StatusCode >= 500),
                    group.Count(call => call.StatusCode is >= 400 and < 500),
                    Percentile(durations, 0.50),
                    Percentile(durations, 0.95),
                    durations[^1],
                    Seconds(durations.Sum(value => (long)value)),
                    group.Sum(call => call.SapCalls),
                    Seconds(group.Sum(call => (long)call.SapMs)),
                    [.. group
                        .GroupBy(call => call.Caller, StringComparer.Ordinal)
                        .Select(callers => new InboundCallerShare(callers.Key, callers.Count()))
                        .OrderByDescending(share => share.Calls)
                        .ThenBy(share => share.Caller, StringComparer.Ordinal)
                        .Take(TopCallersPerRoute)]);
            })
            .OrderByDescending(route => route.Calls)
            .ThenByDescending(route => route.TotalSeconds)
            .ThenBy(route => route.Route, StringComparer.Ordinal)
            .Take(RouteCount)
    ];

    private static List<InboundCallerTiming> Callers(List<InboundCallRecord> calls) =>
    [
        .. calls
            .GroupBy(call => call.Caller, StringComparer.Ordinal)
            .Select(group =>
            {
                var durations = group.Select(call => call.DurationMs).Order().ToList();
                return new InboundCallerTiming(
                    group.Key,
                    durations.Count,
                    group.Count(call => call.StatusCode >= 500),
                    Percentile(durations, 0.95),
                    Seconds(durations.Sum(value => (long)value)));
            })
            .OrderByDescending(caller => caller.Calls)
            .ThenBy(caller => caller.Caller, StringComparer.Ordinal)
    ];

    private static double Seconds(long milliseconds) => Math.Round(milliseconds / 1000d, 1);

    private static int Milliseconds(TimeSpan value) =>
        (int)Math.Clamp(Math.Round(value.TotalMilliseconds), 0, int.MaxValue);
}
