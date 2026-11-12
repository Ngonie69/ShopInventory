namespace ShopInventory.Services;

/// <summary>
/// A background SQLQueries read that was not sent, because SAP took an identical one, did not answer
/// it, and is presumed to be still working on it. See <see cref="SapStuckReadState"/>.
/// </summary>
/// <remarks>
/// Reaches callers as a timeout and is not retried; see <see cref="SapReadNotSentException"/>.
/// </remarks>
public sealed class SapReadStillRunningException(string path, DateTimeOffset heldUntil)
    : SapReadNotSentException(
        $"Not sent to SAP: an identical read of {path} got no answer and SAP is presumed to be still working on it, until {heldUntil:HH:mm:ss} UTC.")
{
    /// <summary>When the hold ends and the read will be sent again.</summary>
    public DateTimeOffset HeldUntil { get; } = heldUntil;
}
