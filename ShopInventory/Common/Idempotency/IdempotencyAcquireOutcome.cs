namespace ShopInventory.Common.Idempotency;

public enum IdempotencyAcquireOutcome
{
    Acquired = 0,
    ReplayAvailable = 1,
    InProgress = 2,
    RequestMismatch = 3
}