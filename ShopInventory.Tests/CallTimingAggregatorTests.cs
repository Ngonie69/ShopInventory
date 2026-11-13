using ShopInventory.Features.CallTimings.Queries.GetCallTimings;
using ShopInventory.Services.CallTimings;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the arithmetic of the Admin call timings page: which SAP calls count toward a family's
/// percentiles, how slot waits are summed, what the slowest list and the route table hold, and from when
/// the page says its figures run.
/// </summary>
/// <remarks>
/// Driven with records written by hand, so each figure can be checked against a number worked out here.
/// </remarks>
public class CallTimingAggregatorTests
{
    private static readonly DateTime Now = new(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowStart = Now.AddHours(-1);
    private static readonly DateTime RecordingSince = Now.AddHours(-6);

    // ---- Percentiles ------------------------------------------------------------------------------

    [Theory]
    [InlineData(new int[0], 0.95, 0)]
    [InlineData(new[] { 7 }, 0.50, 7)]
    [InlineData(new[] { 7 }, 0.95, 7)]
    [InlineData(new[] { 1, 2, 3, 4 }, 0.50, 2)]
    [InlineData(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, 0.50, 5)]
    [InlineData(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, 0.95, 10)]
    [InlineData(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, 0.90, 9)]
    public void Percentiles_are_nearest_rank(int[] ascending, double fraction, int expected)
    {
        Assert.Equal(expected, CallTimingAggregator.Percentile(ascending, fraction));
    }

    [Fact]
    public void A_familys_percentiles_are_of_the_time_at_sap_over_the_calls_that_reached_it()
    {
        // Twenty answered reads at 100..2000 ms at SAP, each having queued 5 s for a slot first, plus two
        // refused reads and one that gave up while still queued: none of the last three reached SAP.
        var calls = Enumerable.Range(1, 20)
            .Select(i => Sap("STK_ITEMS", atSapMs: i * 100, slotWaitMs: 5000, minutesAgo: i))
            .Append(Sap("STK_ITEMS", atSapMs: 0, outcome: SapCallOutcome.NotSent, status: 0))
            .Append(Sap("STK_ITEMS", atSapMs: 0, outcome: SapCallOutcome.NotSent, status: 0))
            .Append(Sap("STK_ITEMS", atSapMs: 0, slotWaitMs: 30_000, outcome: SapCallOutcome.GaveUp, status: 0))
            .ToList();

        var family = Single(Aggregate(sap: calls).Families);

        Assert.Equal(23, family.Calls);
        Assert.Equal(20, family.Ok);
        Assert.Equal(2, family.NotSent);
        Assert.Equal(1, family.GaveUp);
        Assert.Equal(1000, family.P50Ms);
        Assert.Equal(1900, family.P95Ms);
        Assert.Equal(2000, family.MaxMs);
        Assert.Equal(21.0, family.TotalSeconds);
        Assert.Equal(21, family.Waited);
        Assert.Equal(30_000, family.MaxWaitMs);
    }

    [Fact]
    public void Each_outcome_and_each_presumed_stuck_read_is_counted_once()
    {
        var calls = new List<SapCallRecord>
        {
            Sap("WHS_BATCHES", 40),
            Sap("WHS_BATCHES", 500, outcome: SapCallOutcome.Error, status: 500),
            Sap("WHS_BATCHES", 300_000, outcome: SapCallOutcome.Timeout, status: 504, stuck: true),
            Sap("WHS_BATCHES", 60_000, outcome: SapCallOutcome.GaveUp, status: 0, stuck: true),
            Sap("WHS_BATCHES", 0, outcome: SapCallOutcome.NotSent, status: 0),
        };

        var family = Single(Aggregate(sap: calls).Families);

        Assert.Equal((5, 1, 1, 1, 1, 1, 2), (family.Calls, family.Ok, family.Errors, family.Timeouts, family.GaveUp, family.NotSent, family.PresumedStuck));
    }

    [Fact]
    public void Calls_before_the_window_are_left_out()
    {
        var calls = new List<SapCallRecord>
        {
            Sap("STK_ITEMS", 100, minutesAgo: 10),
            Sap("STK_ITEMS", 900_000, minutesAgo: 61),
        };
        var inbound = new List<InboundCallRecord>
        {
            Inbound("GET", "api/Stock/{warehouseCode}", 10, minutesAgo: 59),
            Inbound("GET", "api/Stock/{warehouseCode}", 10, minutesAgo: 90),
        };

        var result = Aggregate(sap: calls, inbound: inbound);

        Assert.Equal(1, result.SapCalls);
        Assert.Equal(100, Single(result.Families).MaxMs);
        Assert.Equal(1, result.InboundCalls);
        Assert.Equal(1, Single(result.Routes).Calls);
    }

    // ---- Families ---------------------------------------------------------------------------------

    [Fact]
    public void Families_come_in_order_of_time_at_sap_and_a_family_with_only_reads_in_flight_is_listed()
    {
        var calls = new List<SapCallRecord>
        {
            Sap("POST Invoices", 800),
            Sap("STK_ITEMS", 50),
            Sap("STK_ITEMS", 50),
            Sap("DSK_INV_COST_RANGE", 4_000),
        };
        var inFlight = new List<SapCallInFlightTiming>
        {
            InFlight("WHS_BATCHES", ageMs: 90_000),
            InFlight("STK_ITEMS", ageMs: 1_000),
        };

        var families = Aggregate(sap: calls, inFlight: inFlight, limited: ["STK_ITEMS", "WHS_BATCHES", "DSK_INV_COST_RANGE"]).Families;

        Assert.Equal(["DSK_INV_COST_RANGE", "POST Invoices", "STK_ITEMS", "WHS_BATCHES"], families.Select(family => family.Family));
        var batches = families.Single(family => family.Family == "WHS_BATCHES");
        Assert.Equal((0, 1, true), (batches.Calls, batches.InFlightNow, batches.Limited));
        Assert.Equal(1, families.Single(family => family.Family == "STK_ITEMS").InFlightNow);
        Assert.False(families.Single(family => family.Family == "POST Invoices").Limited);
    }

    // ---- Slowest ----------------------------------------------------------------------------------

    [Fact]
    public void The_slowest_list_is_the_25_longest_calls_waits_included_with_their_outcome_and_origin()
    {
        var calls = Enumerable.Range(1, 40).Select(i => Sap("STK_ITEMS", atSapMs: i)).ToList();
        calls.Add(Sap("STOCK_QTY", atSapMs: 10, slotWaitMs: 55_700, origin: "GET api/Stock/{warehouseCode}"));
        calls.Add(Sap("WHS_BATCHES", atSapMs: 300_000, outcome: SapCallOutcome.Timeout, status: 504, stuck: true));

        var slowest = Aggregate(sap: calls).Slowest;

        Assert.Equal(CallTimingAggregator.SlowestCount, slowest.Count);
        Assert.Equal(("WHS_BATCHES", "timeout", 504, true), (slowest[0].Family, slowest[0].Outcome, slowest[0].StatusCode, slowest[0].PresumedStuck));
        Assert.Equal(("STOCK_QTY", 55_710, 55_700, 10), (slowest[1].Family, slowest[1].DurationMs, slowest[1].SlotWaitMs, slowest[1].AtSapMs));
        Assert.Equal("GET api/Stock/{warehouseCode}", slowest[1].Origin);
        Assert.Equal(40, slowest[2].DurationMs);
        Assert.Equal(DateTimeKind.Utc, slowest[0].StartedUtc.Kind);
    }

    // ---- Slot waits -------------------------------------------------------------------------------

    [Fact]
    public void Slot_waits_add_the_family_wait_to_the_process_wide_wait_and_split_by_priority()
    {
        var calls = new List<SapCallRecord>
        {
            Sap("STK_ITEMS", 10, familyWaitMs: 1_000, slotWaitMs: 1_500, interactive: true),
            Sap("STK_ITEMS", 10, slotWaitMs: 99, interactive: true),
            Sap("WHS_BATCHES", 10, familyWaitMs: 5_000, interactive: false),
            Sap("WHS_BATCHES", 10, interactive: false),
        };

        var waits = Aggregate(sap: calls).SlotWaits;

        Assert.Equal((4, 2, 2, 5_000, 5_000, 1_500, 7.6), (waits.Calls, waits.Waited, waits.WaitedOverTwoSeconds, waits.MaxMs, waits.MaxFamilyWaitMs, waits.MaxSlotWaitMs, waits.TotalSeconds));
        var interactive = waits.ByPriority.Single(priority => priority.Priority == "interactive");
        var background = waits.ByPriority.Single(priority => priority.Priority == "background");
        Assert.Equal((2, 1, 2_500), (interactive.Calls, interactive.Waited, interactive.MaxMs));
        Assert.Equal((2, 1, 5_000), (background.Calls, background.Waited, background.MaxMs));
    }

    // ---- Inbound ----------------------------------------------------------------------------------

    [Fact]
    public void Routes_group_by_method_and_template_count_errors_and_name_their_busiest_callers()
    {
        var inbound = new List<InboundCallRecord>();
        for (var i = 1; i <= 10; i++)
        {
            inbound.Add(Inbound("GET", "api/Product/warehouse/{warehouseCode}/paged", i * 100,
                caller: i <= 6 ? "Handset · kefalos-vansales" : "Web", status: (short)(i == 10 ? 500 : 200), sapCalls: 1, sapMs: i * 50));
        }

        inbound.Add(Inbound("POST", "api/Product/warehouse/{warehouseCode}/paged", 5, status: 404, caller: "Anonymous"));
        inbound.Add(Inbound("GET", "api/Invoice/{docEntry}", 5, caller: "Web"));
        inbound.Add(Inbound("GET", "api/Invoice/{docEntry}", 5, caller: "Till / bearer only", status: 401));

        var result = Aggregate(inbound: inbound);

        var paged = result.Routes[0];
        Assert.Equal(("GET", "api/Product/warehouse/{warehouseCode}/paged", 10), (paged.Method, paged.Route, paged.Calls));
        Assert.Equal((1, 0, 500, 1000, 1000, 5.5, 10, 2.8), (paged.ServerErrors, paged.ClientErrors, paged.P50Ms, paged.P95Ms, paged.MaxMs, paged.TotalSeconds, paged.SapCalls, paged.SapSeconds));
        Assert.Equal([("Handset · kefalos-vansales", 6), ("Web", 4)], paged.TopCallers.Select(share => (share.Caller, share.Calls)));

        Assert.Equal(("GET", "api/Invoice/{docEntry}", 2, 1), (result.Routes[1].Method, result.Routes[1].Route, result.Routes[1].Calls, result.Routes[1].ClientErrors));
        Assert.Equal(("POST", 1, 1), (result.Routes[2].Method, result.Routes[2].Calls, result.Routes[2].ClientErrors));

        Assert.Equal(["Handset · kefalos-vansales", "Web", "Anonymous", "Till / bearer only"], result.Callers.Select(caller => caller.Caller));
        Assert.Equal((5, 1), (result.Callers[1].Calls, result.Callers[1].ServerErrors));
    }

    // ---- Coverage ---------------------------------------------------------------------------------

    [Fact]
    public void The_figures_start_no_earlier_than_the_process_and_no_earlier_than_the_oldest_kept_call_once_calls_were_dropped()
    {
        var today = Aggregate(windowStart: Now.AddHours(-10));
        Assert.Equal(RecordingSince, today.SapCoverageStartUtc);

        var full = Aggregate(windowStart: Now.AddHours(-10), sapDropped: true, sapOldest: Now.AddMinutes(-20));
        Assert.Equal(Now.AddMinutes(-20), full.SapCoverageStartUtc);
        Assert.Equal(RecordingSince, full.InboundCoverageStartUtc);

        var lastHour = Aggregate(sapDropped: true, sapOldest: Now.AddHours(-3));
        Assert.Equal(WindowStart, lastHour.SapCoverageStartUtc);
    }

    [Fact]
    public void Calls_in_flight_are_listed_oldest_first()
    {
        var inFlight = new List<SapCallInFlightTiming>
        {
            InFlight("STK_ITEMS", 2_000),
            InFlight("WHS_BATCHES", 240_000),
            InFlight("POST Invoices", 30_000),
        };

        Assert.Equal([240_000, 30_000, 2_000], Aggregate(inFlight: inFlight).InFlight.Select(call => call.AgeMs));
    }

    [Theory]
    [InlineData("15m", 2026, 11, 13, 9, 45)]
    [InlineData("1h", 2026, 11, 13, 9, 0)]
    [InlineData("today", 2026, 11, 12, 22, 0)]
    [InlineData("TODAY", 2026, 11, 12, 22, 0)]
    public void Today_starts_at_midnight_cat(string window, int year, int month, int day, int hour, int minute)
    {
        Assert.Equal(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc), CallTimingWindows.StartOf(window, Now));
    }

    [Fact]
    public void Only_the_three_windows_are_accepted()
    {
        var validator = new GetCallTimingsValidator();

        Assert.All(CallTimingWindows.All, window => Assert.True(validator.Validate(new GetCallTimingsQuery(window)).IsValid));
        Assert.False(validator.Validate(new GetCallTimingsQuery("7d")).IsValid);
    }

    // ---- Helpers ----------------------------------------------------------------------------------

    private static T Single<T>(IReadOnlyList<T> items) => Assert.Single(items);

    private static GetCallTimingsResult Aggregate(
        List<SapCallRecord>? sap = null,
        List<InboundCallRecord>? inbound = null,
        List<SapCallInFlightTiming>? inFlight = null,
        string[]? limited = null,
        DateTime? windowStart = null,
        bool sapDropped = false,
        DateTime? sapOldest = null)
    {
        var slots = new SapSlotUsage(6, 0, 4, 0,
            [.. (limited ?? []).Select(family => new SapReadFamilySlots(family, 2, 0, 0, null))]);

        return CallTimingAggregator.Aggregate(new CallTimingSnapshot(
            "1h",
            Now,
            windowStart ?? WindowStart,
            RecordingSince,
            sap ?? [],
            sapOldest,
            sapDropped,
            inbound ?? [],
            null,
            false,
            inFlight ?? [],
            slots));
    }

    private static SapCallRecord Sap(
        string family,
        int atSapMs,
        int familyWaitMs = 0,
        int slotWaitMs = 0,
        SapCallOutcome outcome = SapCallOutcome.Ok,
        short status = 200,
        bool stuck = false,
        bool interactive = false,
        int minutesAgo = 5,
        string origin = ApiCallContext.Background) =>
        new(
            Now.AddMinutes(-minutesAgo).Ticks,
            familyWaitMs + slotWaitMs + atSapMs,
            familyWaitMs,
            slotWaitMs,
            atSapMs,
            family,
            family,
            origin,
            outcome,
            status,
            interactive,
            stuck);

    private static InboundCallRecord Inbound(
        string method,
        string route,
        int durationMs,
        string caller = "Web",
        short status = 200,
        int minutesAgo = 5,
        int sapCalls = 0,
        int sapMs = 0) =>
        new(Now.AddMinutes(-minutesAgo).Ticks, durationMs, status, method, route, caller, sapCalls, sapMs);

    private static SapCallInFlightTiming InFlight(string family, int ageMs) =>
        new(Now.AddMilliseconds(-ageMs), ageMs, family, family, "at-sap", ageMs, false, ApiCallContext.Background);
}
