namespace ShopInventory.Middleware;

/// <summary>
/// Ambient marker for SAP calls a person is waiting on, so <see cref="SAPConcurrencyHandler"/> can
/// keep them out of the queue that bulk and polling work builds up.
/// </summary>
/// <remarks>
/// The SAP concurrency limit is process-wide and first-come-first-served, and most of the traffic
/// through it is background work nobody is watching — POD link validation, invoice lists, catalog
/// sync. A sales order approval issues a series of round-trips and pays the full queue wait on
/// every one of them, which is how an approval that should take seconds ended up taking minutes.
/// Marking that path interactive reserves capacity it cannot be crowded out of.
/// </remarks>
public static class SapRequestPriority
{
    private static readonly AsyncLocal<int> Depth = new();

    /// <summary>
    /// True when the current logical call is running inside an <see cref="BeginInteractive"/>
    /// scope. Flows across awaits, so a single scope at the top of a request covers every SAP call
    /// that request makes.
    /// </summary>
    public static bool IsInteractive => Depth.Value > 0;

    /// <summary>
    /// Marks everything up to the returned scope's disposal as interactive. Nesting is counted, so
    /// an inner scope does not end an outer one.
    /// </summary>
    public static IDisposable BeginInteractive()
    {
        var previous = Depth.Value;
        Depth.Value = previous + 1;
        return new Scope(previous);
    }

    /// <summary>
    /// Drops the ambient priority for the current logical call, so work started from a request
    /// thread but not awaited by it — fire-and-forget bulk operations — does not inherit the
    /// caller's reservation.
    /// </summary>
    /// <remarks>
    /// <see cref="Depth"/> is an <see cref="AsyncLocal{T}"/>, so it flows into anything started
    /// inside a request, including a <c>Task.Run</c> the request never awaits. Marking the endpoint
    /// with <see cref="SapBackgroundWorkAttribute"/> is the primary control; this is for the case
    /// where one endpoint does both, and for making the intent explicit at the hand-off.
    /// </remarks>
    public static IDisposable SuppressInteractive()
    {
        var previous = Depth.Value;
        Depth.Value = 0;
        return new Scope(previous);
    }

    private sealed class Scope(int previousDepth) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Depth.Value = previousDepth;
        }
    }
}
