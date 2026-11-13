namespace ShopInventory.Features.CallTimings.Queries.GetCallTimings;

/// <summary>What the Admin call timings page shows. Every time is UTC; every duration is milliseconds.</summary>
public sealed record GetCallTimingsResult
{
    /// <summary>False when <c>CallTimings:Enabled</c> is off; every list is then empty.</summary>
    public required bool Enabled { get; init; }

    public required string Window { get; init; }

    public required DateTime GeneratedAtUtc { get; init; }

    /// <summary>Where the window starts. The figures may start later: see the coverage fields.</summary>
    public required DateTime WindowStartUtc { get; init; }

    /// <summary>When this API process began recording. A recycle starts the figures again.</summary>
    public required DateTime RecordingSinceUtc { get; init; }

    /// <summary>
    /// When the SAP figures actually start: the latest of the window's start, the process start and,
    /// when the ring has dropped calls, its oldest kept call.
    /// </summary>
    public required DateTime SapCoverageStartUtc { get; init; }

    public required DateTime InboundCoverageStartUtc { get; init; }

    public required int SapCalls { get; init; }

    public required int InboundCalls { get; init; }

    public required SapSlotUsage Slots { get; init; }

    public required SapSlotWaitSummary SlotWaits { get; init; }

    public required IReadOnlyList<SapFamilyTiming> Families { get; init; }

    public required IReadOnlyList<SapCallTiming> Slowest { get; init; }

    public required IReadOnlyList<SapCallInFlightTiming> InFlight { get; init; }

    public required IReadOnlyList<InboundRouteTiming> Routes { get; init; }

    public required IReadOnlyList<InboundCallerTiming> Callers { get; init; }
}
