namespace ShopInventory.Services.CallTimings;

/// <summary>One finished SAP Service Layer call, as <see cref="CallTimingRecorder"/> keeps it.</summary>
/// <param name="StartedUtcTicks">When the call entered the SAP pipeline, UTC ticks.</param>
/// <param name="DurationMs">From entering the pipeline to the end, waits included.</param>
/// <param name="FamilyWaitMs">Time queued for a slot of its read family (<c>SAP:ReadFamilies</c>).</param>
/// <param name="SlotWaitMs">Time queued for one of the process-wide slots (<c>SAP:MaxConcurrentRequests</c>).</param>
/// <param name="AtSapMs">Time SAP had it: from getting a slot to the end. Zero when it never reached SAP.</param>
/// <param name="Family">The read family or entity the call is grouped under; see <see cref="SapCallLabels"/>.</param>
/// <param name="Operation">What was called, without the query string or any key value.</param>
/// <param name="Origin">The inbound route that made the call, or <see cref="ApiCallContext.Background"/>.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="StatusCode">SAP's HTTP status, or 0 when there was no answer.</param>
/// <param name="Interactive">Whether a person was waiting on it (<c>SapRequestPriority</c>).</param>
/// <param name="PresumedStuck">
/// Whether SAP is presumed to be still working on it after this API stopped waiting — the same rule as
/// the stuck-read hold.
/// </param>
public readonly record struct SapCallRecord(
    long StartedUtcTicks,
    int DurationMs,
    int FamilyWaitMs,
    int SlotWaitMs,
    int AtSapMs,
    string Family,
    string Operation,
    string Origin,
    SapCallOutcome Outcome,
    short StatusCode,
    bool Interactive,
    bool PresumedStuck);
