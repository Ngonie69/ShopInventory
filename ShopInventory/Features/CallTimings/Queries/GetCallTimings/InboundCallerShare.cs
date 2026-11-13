namespace ShopInventory.Features.CallTimings.Queries.GetCallTimings;

/// <summary>How many of a route's requests one kind of client sent.</summary>
public sealed record InboundCallerShare(string Caller, int Calls);
