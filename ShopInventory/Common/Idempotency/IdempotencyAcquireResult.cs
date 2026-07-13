namespace ShopInventory.Common.Idempotency;

public sealed record IdempotencyAcquireResult<TResponse>(
    IdempotencyAcquireOutcome Outcome,
    long? RequestId = null,
    TResponse? Response = default,
    DateTime? CreatedAtUtc = null,
    DateTime? ExpiresAtUtc = null);
