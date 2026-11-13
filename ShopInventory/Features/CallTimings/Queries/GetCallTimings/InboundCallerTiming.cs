namespace ShopInventory.Features.CallTimings.Queries.GetCallTimings;

/// <summary>One kind of client's inbound requests in the window.</summary>
public sealed record InboundCallerTiming(
    string Caller,
    int Calls,
    int ServerErrors,
    int P95Ms,
    double TotalSeconds);
