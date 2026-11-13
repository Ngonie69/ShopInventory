using ShopInventory.Services.CallTimings;

namespace ShopInventory.Features.CallTimings.Queries.GetCallTimings;

/// <summary>Everything <see cref="CallTimingAggregator"/> summarises, read from the recorder and the slot pools at one moment.</summary>
/// <param name="Window">The window asked for.</param>
/// <param name="NowUtc">The moment the snapshot was taken.</param>
/// <param name="WindowStartUtc">Where the window starts.</param>
/// <param name="RecordingSinceUtc">When this process began recording.</param>
/// <param name="SapCalls">Finished SAP calls; any before <paramref name="WindowStartUtc"/> are ignored.</param>
/// <param name="SapOldestKeptUtc">The oldest SAP call the ring still holds, or null when it holds none.</param>
/// <param name="SapDropped">Whether the SAP ring has dropped calls to make room.</param>
/// <param name="InboundCalls">Finished inbound requests; any before <paramref name="WindowStartUtc"/> are ignored.</param>
/// <param name="InboundOldestKeptUtc">The oldest inbound request the ring still holds.</param>
/// <param name="InboundDropped">Whether the inbound ring has dropped requests to make room.</param>
/// <param name="InFlight">SAP calls in the pipeline right now.</param>
/// <param name="Slots">The process-wide and read-family slots right now.</param>
public sealed record CallTimingSnapshot(
    string Window,
    DateTime NowUtc,
    DateTime WindowStartUtc,
    DateTime RecordingSinceUtc,
    IReadOnlyList<SapCallRecord> SapCalls,
    DateTime? SapOldestKeptUtc,
    bool SapDropped,
    IReadOnlyList<InboundCallRecord> InboundCalls,
    DateTime? InboundOldestKeptUtc,
    bool InboundDropped,
    IReadOnlyList<SapCallInFlightTiming> InFlight,
    SapSlotUsage Slots);
