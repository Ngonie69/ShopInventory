namespace ShopInventory.Services.CallTimings;

/// <summary>
/// The inbound request a SAP call is made for, so the call timings page can say which route sent a
/// stuck read, and so each inbound request can count the SAP time it spent.
/// </summary>
/// <remarks>
/// An <see cref="AsyncLocal{T}"/> set by <c>InboundCallTimingMiddleware</c>. Like
/// <c>SapRequestPriority</c> it flows into work a request starts and does not await; such work is
/// labelled with the route that started it, which is where it came from.
/// </remarks>
public sealed class ApiCallContext(string route)
{
    /// <summary>The origin of a SAP call made outside any inbound request: jobs, queues, startup.</summary>
    public const string Background = "(background)";

    private static readonly AsyncLocal<ApiCallContext?> CurrentContext = new();

    private int _sapCalls;
    private long _sapMs;

    /// <summary>The request's method and route template, for example <c>GET api/Invoice/{docEntry}</c>.</summary>
    public string Route { get; } = route;

    public int SapCalls => Volatile.Read(ref _sapCalls);

    public int SapMs => (int)Math.Min(int.MaxValue, Interlocked.Read(ref _sapMs));

    public static ApiCallContext? Current => CurrentContext.Value;

    /// <summary>Makes <paramref name="context"/> current until the returned scope is disposed.</summary>
    public static IDisposable Begin(ApiCallContext context)
    {
        var previous = CurrentContext.Value;
        CurrentContext.Value = context;
        return new Scope(previous);
    }

    public void AddSapCall(int durationMs)
    {
        Interlocked.Increment(ref _sapCalls);
        Interlocked.Add(ref _sapMs, Math.Max(0, durationMs));
    }

    private sealed class Scope(ApiCallContext? previous) : IDisposable
    {
        public void Dispose() => CurrentContext.Value = previous;
    }
}
