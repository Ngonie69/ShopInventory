namespace ShopInventory.Services.CallTimings;

/// <summary>One finished inbound API request, as <see cref="CallTimingRecorder"/> keeps it.</summary>
/// <param name="StartedUtcTicks">When the request reached the API's pipeline, UTC ticks.</param>
/// <param name="DurationMs">How long the API took to answer it.</param>
/// <param name="StatusCode">The status it was answered with.</param>
/// <param name="Method">The HTTP method.</param>
/// <param name="Route">
/// The route template (<c>api/Invoice/{docEntry}</c>), never the path, so ids, card codes and warehouse
/// codes stay out of it.
/// </param>
/// <param name="Caller">Which kind of client sent it; see <see cref="InboundCallerClassifier"/>.</param>
/// <param name="SapCalls">How many SAP Service Layer calls the request made.</param>
/// <param name="SapMs">How long those calls took, added up.</param>
public readonly record struct InboundCallRecord(
    long StartedUtcTicks,
    int DurationMs,
    short StatusCode,
    string Method,
    string Route,
    string Caller,
    int SapCalls,
    int SapMs);
