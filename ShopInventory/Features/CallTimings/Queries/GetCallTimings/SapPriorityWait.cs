namespace ShopInventory.Features.CallTimings.Queries.GetCallTimings;

/// <summary>Slot waits for one priority: <c>interactive</c> (a person is waiting) or <c>background</c>.</summary>
public sealed record SapPriorityWait(string Priority, int Calls, int Waited, int P95Ms, int MaxMs);
