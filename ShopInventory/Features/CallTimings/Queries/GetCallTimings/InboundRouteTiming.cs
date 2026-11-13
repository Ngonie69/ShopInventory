namespace ShopInventory.Features.CallTimings.Queries.GetCallTimings;

/// <summary>One route's inbound requests in the window.</summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="Route">The route template, never a path.</param>
/// <param name="Calls">Requests in the window.</param>
/// <param name="ServerErrors">Answered 5xx.</param>
/// <param name="ClientErrors">Answered 4xx.</param>
/// <param name="P50Ms">Median time to answer.</param>
/// <param name="P95Ms">95th percentile of the time to answer.</param>
/// <param name="MaxMs">The longest time to answer.</param>
/// <param name="TotalSeconds">Time to answer, added up.</param>
/// <param name="SapCalls">SAP calls the route's requests made.</param>
/// <param name="SapSeconds">SAP call time the route's requests spent, added up.</param>
/// <param name="TopCallers">The kinds of client that called it most, busiest first.</param>
public sealed record InboundRouteTiming(
    string Method,
    string Route,
    int Calls,
    int ServerErrors,
    int ClientErrors,
    int P50Ms,
    int P95Ms,
    int MaxMs,
    double TotalSeconds,
    int SapCalls,
    double SapSeconds,
    IReadOnlyList<InboundCallerShare> TopCallers);
