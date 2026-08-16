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

    /// <summary>
    /// Gives a claim back when the work it guarded did not complete.
    /// </summary>
    /// <remarks>
    /// Call this with <see cref="CancellationToken.None"/>, never the request's token. The commonest
    /// reason the work did not complete is that the request was cancelled — a client that gave up
    /// while SAP was slow — and that is precisely when the request token would cancel the release
    /// too. The claim then stays "in progress" for the whole expiry window, and every retry under the
    /// same key is refused as a duplicate of a request that no longer exists. The delete is one
    /// bounded statement, so running it to completion costs nothing worth saving.
    /// </remarks>
    Task ReleaseAsync(
        long requestId,
        CancellationToken cancellationToken);
}
