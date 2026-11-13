namespace ShopInventory.Features.CallTimings.Queries.GetCallTimings;

/// <summary>A SAP call still in the pipeline.</summary>
/// <param name="StartedUtc">When it entered the SAP pipeline.</param>
/// <param name="AgeMs">How long it has been in the pipeline.</param>
/// <param name="Family">The family it is grouped under.</param>
/// <param name="Operation">What is being called, with no query string or key value.</param>
/// <param name="Stage">family-slot (queued for its read family), slot (queued for a process-wide slot) or at-sap.</param>
/// <param name="AtSapMs">How long SAP has had it; zero while it is queued.</param>
/// <param name="Interactive">Whether a person is waiting on it.</param>
/// <param name="Origin">The inbound route that made it, or <c>(background)</c>.</param>
public sealed record SapCallInFlightTiming(
    DateTime StartedUtc,
    int AgeMs,
    string Family,
    string Operation,
    string Stage,
    int AtSapMs,
    bool Interactive,
    string Origin);
