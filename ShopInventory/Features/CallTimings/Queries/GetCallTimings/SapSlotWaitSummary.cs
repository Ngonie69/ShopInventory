namespace ShopInventory.Features.CallTimings.Queries.GetCallTimings;

/// <summary>How long SAP calls in the window queued for a slot, read-family and process-wide together.</summary>
/// <param name="Calls">SAP calls in the window.</param>
/// <param name="Waited">Calls that queued 100 ms or more.</param>
/// <param name="WaitedOverTwoSeconds">Calls that queued 2 s or more, the threshold the API log warns at.</param>
/// <param name="P95Ms">95th percentile of the time queued, over every call.</param>
/// <param name="MaxMs">The longest time queued.</param>
/// <param name="MaxFamilyWaitMs">The longest time queued for a read-family slot.</param>
/// <param name="MaxSlotWaitMs">The longest time queued for a process-wide slot.</param>
/// <param name="TotalSeconds">Time queued, added up.</param>
/// <param name="ByPriority">The same, for calls a person waited on and for background work.</param>
public sealed record SapSlotWaitSummary(
    int Calls,
    int Waited,
    int WaitedOverTwoSeconds,
    int P95Ms,
    int MaxMs,
    int MaxFamilyWaitMs,
    int MaxSlotWaitMs,
    double TotalSeconds,
    IReadOnlyList<SapPriorityWait> ByPriority);
