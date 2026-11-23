namespace ShopInventory.Services;

/// <summary>
/// Tells a caller that the SAP read it lost was one SAP took and never answered, so that it does not
/// ask for the same thing again.
/// </summary>
/// <remarks>
/// <para>
/// A stored-query read that stalls leaves a Service Layer process working on it for good
/// (<see cref="SapStuckReadState"/>). A failed connection or a server error leaves nothing behind and may
/// be tried again. Both reach a caller as an exception, and the type cannot say which: the stalled one
/// is an <see cref="OperationCanceledException"/> from the caller's own budget, or the load balancer's
/// 502 or 504 wrapped in a <see cref="SapReadFailedException"/>.
/// </para>
/// <para>
/// So <c>SAPStuckReadHandler</c>, which knows whether the read had reached SAP and for how long, marks
/// the exception. A mark on <see cref="Exception.Data"/> rather than a type of its own, for the reason
/// <see cref="SapFailureClassifier.MarkNotSent"/> is one: every caller of the client catches by type,
/// and a wrapper would change which of those catches runs.
/// </para>
/// </remarks>
public static class SapStalledRead
{
    private const string Key = "ShopInventory.SapReadPresumedStuck";

    /// <summary>Records on <paramref name="exception"/> that SAP took the read and is presumed stuck on it.</summary>
    public static void Mark(Exception exception) => exception.Data[Key] = true;

    /// <summary>
    /// True when <paramref name="exception"/>, or anything it wraps, is a stored-query read SAP is presumed
    /// stuck on: one that just stalled, or one refused because an identical read stalled earlier
    /// (<see cref="SapReadStillRunningException"/>).
    /// </summary>
    public static bool IsPresumedStuck(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SapReadStillRunningException || current.Data[Key] is true)
            {
                return true;
            }
        }

        return false;
    }
}
