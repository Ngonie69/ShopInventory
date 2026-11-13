namespace ShopInventory.Features.CallTimings.Queries.GetCallTimings;

/// <summary>The process-wide SAP slots right now (<c>SAP:MaxConcurrentRequests</c>), and each limited read family's.</summary>
public sealed record SapSlotUsage(
    int Limit,
    int InUse,
    int BackgroundLimit,
    int BackgroundInUse,
    IReadOnlyList<SapReadFamilySlots> Families);
