namespace ShopInventory.Services.CallTimings;

/// <summary>How one SAP Service Layer call ended, as the call timings page groups it.</summary>
public enum SapCallOutcome : byte
{
    /// <summary>SAP answered with a success status.</summary>
    Ok,

    /// <summary>SAP answered with an error status other than 504, or the connection failed.</summary>
    Error,

    /// <summary>
    /// No answer in time: the client's timeout or a breaker deadline ran out, or the load balancer
    /// answered 504.
    /// </summary>
    Timeout,

    /// <summary>
    /// The caller stopped waiting before SAP answered: a closed page, an aborted request, or a budget of
    /// the caller's own such as the stock read's 60 seconds. SAP may still be working on it.
    /// </summary>
    GaveUp,

    /// <summary>
    /// Refused before it reached SAP: an identical read is still running there, its read family was full,
    /// or the circuit breaker was open.
    /// </summary>
    NotSent,
}
