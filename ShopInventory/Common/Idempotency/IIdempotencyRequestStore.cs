namespace ShopInventory.Common.Idempotency;

public interface IIdempotencyRequestStore
{
    Task<IdempotencyAcquireResult<TResponse>> TryAcquireAsync<TResponse>(
        string scope,
        string key,
        object request,
        CancellationToken cancellationToken);

    Task CompleteAsync<TResponse>(
        long requestId,
        TResponse response,
        CancellationToken cancellationToken);

    Task ReleaseAsync(
        long requestId,
        CancellationToken cancellationToken);
}