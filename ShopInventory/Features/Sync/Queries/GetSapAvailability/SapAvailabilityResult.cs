namespace ShopInventory.Features.Sync.Queries.GetSapAvailability;

/// <summary>
/// Whether SAP is down as the cluster sees it, and how much trading is waiting for it.
/// </summary>
/// <param name="IsDown">An outage is open.</param>
/// <param name="Cause"><c>Unreachable</c> or <c>SwitchedOff</c>; null while SAP is up and no outage just ended.</param>
/// <param name="SinceUtc">When the open or just-ended outage started.</param>
/// <param name="EndedAtUtc">When the outage ended, for the short "SAP is back" notice. Null while it is open.</param>
/// <param name="SalesAwaitingSap">
/// Sales recorded since the outage started that SAP does not have yet. Zero when there is no outage to
/// report on.
/// </param>
public sealed record SapAvailabilityResult(
    bool IsDown,
    string? Cause,
    DateTime? SinceUtc,
    DateTime? EndedAtUtc,
    int SalesAwaitingSap)
{
    public static readonly SapAvailabilityResult Available = new(false, null, null, null, 0);
}
