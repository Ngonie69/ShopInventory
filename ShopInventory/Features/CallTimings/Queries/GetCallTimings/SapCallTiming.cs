namespace ShopInventory.Features.CallTimings.Queries.GetCallTimings;

/// <summary>One finished SAP call, for the slowest list.</summary>
/// <param name="StartedUtc">When it entered the SAP pipeline.</param>
/// <param name="Family">The family it is grouped under.</param>
/// <param name="Operation">What was called, with no query string or key value.</param>
/// <param name="DurationMs">From entering the pipeline to the end, waits included.</param>
/// <param name="AtSapMs">How long SAP had it.</param>
/// <param name="FamilyWaitMs">Time queued for a read-family slot.</param>
/// <param name="SlotWaitMs">Time queued for a process-wide slot.</param>
/// <param name="Outcome">ok, error, timeout, gave-up or not-sent.</param>
/// <param name="StatusCode">SAP's HTTP status, or 0 when there was no answer.</param>
/// <param name="Interactive">Whether a person was waiting on it.</param>
/// <param name="PresumedStuck">Whether SAP is presumed to be still working on it.</param>
/// <param name="Origin">The inbound route that made it, or <c>(background)</c>.</param>
public sealed record SapCallTiming(
    DateTime StartedUtc,
    string Family,
    string Operation,
    int DurationMs,
    int AtSapMs,
    int FamilyWaitMs,
    int SlotWaitMs,
    string Outcome,
    int StatusCode,
    bool Interactive,
    bool PresumedStuck,
    string Origin);
