namespace ShopInventory.Features.CallTimings.Queries.GetCallTimings;

/// <summary>One limited read family's slots right now.</summary>
/// <param name="Family">The family's name, which is also its query-code prefix.</param>
/// <param name="Limit">How many of its reads may be at SAP at once.</param>
/// <param name="InUse">Slots taken, kept ones included.</param>
/// <param name="Kept">Slots kept for reads SAP is presumed to be still working on.</param>
/// <param name="FirstKeptSlotFreesAtUtc">When the first kept slot comes free, or null when none is kept.</param>
public sealed record SapReadFamilySlots(
    string Family,
    int Limit,
    int InUse,
    int Kept,
    DateTime? FirstKeptSlotFreesAtUtc);
