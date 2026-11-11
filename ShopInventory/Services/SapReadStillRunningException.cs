namespace ShopInventory.Services;

/// <summary>
/// A background SQLQueries read that was not sent, because SAP took an identical one, did not answer
/// it, and is presumed to be still working on it. See <see cref="SapStuckReadState"/>.
/// </summary>
/// <remarks>
/// A <see cref="TimeoutException"/> on purpose. Sent, the read would most likely have run out its
/// budget beside the stuck one, and every caller already handles a read that timed out; this one
/// reaches them the same way, only without the wait. <see cref="SapFailureClassifier"/> reads it as
/// transient, so queued work tries again later. The client's transient retry does not resend it:
/// the hold outlasts every retry it has.
/// </remarks>
public sealed class SapReadStillRunningException(string path, DateTimeOffset heldUntil)
    : TimeoutException(
        $"Not sent to SAP: an identical read of {path} got no answer and SAP is presumed to be still working on it, until {heldUntil:HH:mm:ss} UTC.")
{
    /// <summary>When the hold ends and the read will be sent again.</summary>
    public DateTimeOffset HeldUntil { get; } = heldUntil;
}
