namespace ShopInventory.Services.CallTimings;

/// <summary>
/// A SAP call still in the pipeline. Created by <c>SAPCallTimingHandler</c>; the read-family and
/// concurrency handlers fill in its waits as it passes them.
/// </summary>
/// <remarks>
/// Its fields are written by the call's own flow and read by the call timings page from another thread,
/// so each is a single volatile field, never a value spread over two.
/// </remarks>
public sealed class SapCallInFlight
{
    private static long _nextId;

    private long _familyWaitTicks = -1;
    private long _reachedSapTimestamp;
    private long _slotWaitTicks = -1;

    public SapCallInFlight(long startedTimestamp, DateTime startedUtc, string family, string operation, string origin, bool interactive)
    {
        Id = Interlocked.Increment(ref _nextId);
        StartedTimestamp = startedTimestamp;
        StartedUtc = startedUtc;
        Family = family;
        Operation = operation;
        Origin = origin;
        Interactive = interactive;
    }

    public long Id { get; }

    /// <summary>When it entered the pipeline, on the pipeline's <see cref="TimeProvider"/> timestamp clock.</summary>
    public long StartedTimestamp { get; }

    public DateTime StartedUtc { get; }

    public string Family { get; }

    public string Operation { get; }

    public string Origin { get; }

    public bool Interactive { get; }

    /// <summary>How long it queued for a read-family slot, once it has stopped queueing; otherwise null.</summary>
    public TimeSpan? FamilyWait =>
        Volatile.Read(ref _familyWaitTicks) is var ticks and >= 0 ? TimeSpan.FromTicks(ticks) : null;

    /// <summary>How long it queued for a process-wide slot, once it has one; otherwise null.</summary>
    public TimeSpan? SlotWait =>
        Volatile.Read(ref _slotWaitTicks) is var ticks and >= 0 ? TimeSpan.FromTicks(ticks) : null;

    /// <summary>When it got a process-wide slot and went to SAP, on the timestamp clock; 0 until then.</summary>
    public long ReachedSapTimestamp => Volatile.Read(ref _reachedSapTimestamp);

    public void FamilyWaited(TimeSpan wait) => Volatile.Write(ref _familyWaitTicks, Math.Max(0, wait.Ticks));

    public void ReachedSap(TimeSpan slotWait, long timestamp)
    {
        Volatile.Write(ref _slotWaitTicks, Math.Max(0, slotWait.Ticks));
        Volatile.Write(ref _reachedSapTimestamp, timestamp == 0 ? 1 : timestamp);
    }

    private static readonly HttpRequestOptionsKey<SapCallInFlight> Key = new("ShopInventory.Sap.CallInFlight");

    /// <summary>Puts <paramref name="call"/> on the request, for the handlers further in.</summary>
    public static void Attach(HttpRequestMessage request, SapCallInFlight call) => request.Options.Set(Key, call);

    /// <summary>The call on the request, or null when timings are off or the request bypassed the timing handler.</summary>
    public static SapCallInFlight? Of(HttpRequestMessage request) =>
        request.Options.TryGetValue(Key, out var call) ? call : null;
}
