using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;

namespace ShopInventory.Services.CallTimings;

/// <summary>
/// Keeps the API's recent inbound requests and SAP Service Layer calls in memory, and the SAP calls still
/// in flight, for the Admin call timings page.
/// </summary>
/// <remarks>
/// <para>
/// Recording is a struct copy into a ring under a lock; nothing is aggregated, logged or written to the
/// database on a request's path. <see cref="ShopInventory.Features.CallTimings.Queries.GetCallTimings.CallTimingAggregator"/> does the arithmetic when the page
/// asks, over whatever window it asks for.
/// </para>
/// <para>
/// In memory and per process, like the SAP slot pool and the stuck-read holds it reports on: the
/// production API is one process, so this is the whole of its traffic. A recycle starts empty, and the
/// page says from when its figures run.
/// </para>
/// </remarks>
public sealed class CallTimingRecorder
{
    private readonly ConcurrentDictionary<long, SapCallInFlight> _inFlight = new();
    private readonly TimeProvider _timeProvider;

    public CallTimingRecorder(IOptions<CallTimingSettings> settings, TimeProvider timeProvider)
    {
        var value = settings.Value;
        _timeProvider = timeProvider;
        Enabled = value.Enabled;
        SapCalls = new TimingRing<SapCallRecord>(value.Enabled ? value.SapCallCapacity : 1);
        InboundCalls = new TimingRing<InboundCallRecord>(value.Enabled ? value.InboundCallCapacity : 1);
        StartedUtc = timeProvider.GetUtcNow().UtcDateTime;
    }

    public bool Enabled { get; }

    /// <summary>When this process began recording.</summary>
    public DateTime StartedUtc { get; }

    public TimingRing<SapCallRecord> SapCalls { get; }

    public TimingRing<InboundCallRecord> InboundCalls { get; }

    public TimeProvider TimeProvider => _timeProvider;

    public IReadOnlyCollection<SapCallInFlight> InFlight => [.. _inFlight.Values];

    public void Began(SapCallInFlight call) => _inFlight[call.Id] = call;

    public void Finished(SapCallInFlight call, in SapCallRecord record)
    {
        _inFlight.TryRemove(call.Id, out _);
        SapCalls.Add(record);
    }

    public void Finished(in InboundCallRecord record) => InboundCalls.Add(record);
}
