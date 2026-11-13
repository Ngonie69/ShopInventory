namespace ShopInventory.Features.CallTimings.Queries.GetCallTimings;

/// <summary>
/// One family's SAP calls in the window. The percentiles are of the time SAP had each call (slot waits
/// left out), over the calls that reached SAP.
/// </summary>
/// <param name="Family">The read family, or the method and entity for any other call.</param>
/// <param name="Limited">Whether the family has a cap in <c>SAP:ReadFamilies</c>.</param>
/// <param name="Calls">Every call of the family in the window, not-sent ones included.</param>
/// <param name="Interactive">Calls a person was waiting on.</param>
/// <param name="Ok">Answered with a success status.</param>
/// <param name="Errors">Answered with an error, or the connection failed.</param>
/// <param name="Timeouts">No answer in time.</param>
/// <param name="GaveUp">The caller stopped waiting first.</param>
/// <param name="NotSent">Refused before reaching SAP.</param>
/// <param name="PresumedStuck">Calls SAP is presumed to be still working on after this API stopped waiting.</param>
/// <param name="P50Ms">Median time at SAP.</param>
/// <param name="P95Ms">95th percentile of the time at SAP.</param>
/// <param name="MaxMs">The longest time at SAP.</param>
/// <param name="TotalSeconds">Time at SAP, added up.</param>
/// <param name="Waited">Calls that queued for a slot 100 ms or more.</param>
/// <param name="WaitP95Ms">95th percentile of the time queued for slots, over every call.</param>
/// <param name="MaxWaitMs">The longest time queued for slots.</param>
/// <param name="InFlightNow">Calls of the family in the pipeline right now.</param>
public sealed record SapFamilyTiming(
    string Family,
    bool Limited,
    int Calls,
    int Interactive,
    int Ok,
    int Errors,
    int Timeouts,
    int GaveUp,
    int NotSent,
    int PresumedStuck,
    int P50Ms,
    int P95Ms,
    int MaxMs,
    double TotalSeconds,
    int Waited,
    int WaitP95Ms,
    int MaxWaitMs,
    int InFlightNow);
