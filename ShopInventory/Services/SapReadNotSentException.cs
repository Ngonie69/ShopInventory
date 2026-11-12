namespace ShopInventory.Services;

/// <summary>
/// A SQLQueries read the SAP pipeline refused to send, because sending it now would most likely leave
/// another Service Layer process stuck on it.
/// </summary>
/// <remarks>
/// A <see cref="TimeoutException"/> on purpose. Sent, the read would most likely have run out its budget,
/// and every caller already handles a read that timed out; this reaches them the same way, only without
/// the wait. <see cref="SapFailureClassifier"/> reads it as transient, so queued work tries again later.
/// The client's transient retry does not resend it: what stopped it lasts minutes, longer than every
/// retry it has.
/// </remarks>
public abstract class SapReadNotSentException(string message) : TimeoutException(message);
