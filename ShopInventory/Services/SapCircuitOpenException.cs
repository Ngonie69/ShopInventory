namespace ShopInventory.Services;

public sealed class SapCircuitOpenException(string message, TimeSpan? retryAfter = null) : InvalidOperationException(message)
{
    /// <summary>
    /// How long the breaker stays open from the moment this was thrown, or null when the thrower did
    /// not know. The transient retry reads it to tell a wait that can land from one that cannot.
    /// </summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
