using ErrorOr;
using MediatR;
using ShopInventory.Middleware;
using ShopInventory.Services;
using ShopInventory.Services.CallTimings;

namespace ShopInventory.Features.CallTimings.Queries.GetCallTimings;

/// <summary>
/// Reads the recorder and the SAP slot pools at one moment and summarises them for the call timings page.
/// </summary>
/// <remarks>
/// Asks nothing of SAP or the database: every figure is this process's own memory, so the page stays
/// readable while SAP is the thing that is stuck.
/// </remarks>
public sealed class GetCallTimingsHandler(
    CallTimingRecorder recorder,
    SapReadFamilyGate gate) : IRequestHandler<GetCallTimingsQuery, ErrorOr<GetCallTimingsResult>>
{
    public Task<ErrorOr<GetCallTimingsResult>> Handle(GetCallTimingsQuery request, CancellationToken cancellationToken)
    {
        var clock = recorder.TimeProvider;
        var nowUtc = clock.GetUtcNow().UtcDateTime;
        var window = request.Window.ToLowerInvariant();
        var windowStartUtc = CallTimingWindows.StartOf(window, nowUtc);
        var slots = Slots();

        if (!recorder.Enabled)
        {
            return Task.FromResult<ErrorOr<GetCallTimingsResult>>(Disabled(window, nowUtc, windowStartUtc, slots));
        }

        var startTicks = windowStartUtc.Ticks;
        var (sapCalls, sapDropped) = recorder.SapCalls.Snapshot(call => call.StartedUtcTicks >= startTicks);
        var (inboundCalls, inboundDropped) = recorder.InboundCalls.Snapshot(call => call.StartedUtcTicks >= startTicks);
        var limited = gate.Families.Select(family => family.Name).ToHashSet(StringComparer.Ordinal);
        var inFlight = recorder.InFlight
            .Select(call => CallTimingAggregator.InFlightRow(call, clock, limited.Contains(call.Family)))
            .ToList();

        var snapshot = new CallTimingSnapshot(
            window,
            nowUtc,
            windowStartUtc,
            recorder.StartedUtc,
            sapCalls,
            Utc(recorder.SapCalls.Oldest()?.StartedUtcTicks),
            sapDropped,
            inboundCalls,
            Utc(recorder.InboundCalls.Oldest()?.StartedUtcTicks),
            inboundDropped,
            inFlight,
            slots);

        return Task.FromResult<ErrorOr<GetCallTimingsResult>>(CallTimingAggregator.Aggregate(snapshot));
    }

    private SapSlotUsage Slots()
    {
        var (limit, inUse, backgroundLimit, backgroundInUse) = SAPConcurrencyHandler.CurrentUsage();
        return new SapSlotUsage(
            limit,
            inUse,
            backgroundLimit,
            backgroundInUse,
            [.. gate.Families
                .OrderBy(family => family.Name, StringComparer.Ordinal)
                .Select(family => new SapReadFamilySlots(
                    family.Name,
                    family.Limit,
                    family.Limit - family.Available,
                    family.Kept,
                    family.FirstKeptSlotFreesAt?.UtcDateTime))]);
    }

    private GetCallTimingsResult Disabled(string window, DateTime nowUtc, DateTime windowStartUtc, SapSlotUsage slots) => new()
    {
        Enabled = false,
        Window = window,
        GeneratedAtUtc = nowUtc,
        WindowStartUtc = windowStartUtc,
        RecordingSinceUtc = recorder.StartedUtc,
        SapCoverageStartUtc = nowUtc,
        InboundCoverageStartUtc = nowUtc,
        SapCalls = 0,
        InboundCalls = 0,
        Slots = slots,
        SlotWaits = new SapSlotWaitSummary(0, 0, 0, 0, 0, 0, 0, 0, []),
        Families = [],
        Slowest = [],
        InFlight = [],
        Routes = [],
        Callers = [],
    };

    private static DateTime? Utc(long? ticks) => ticks is { } value ? new DateTime(value, DateTimeKind.Utc) : null;
}
