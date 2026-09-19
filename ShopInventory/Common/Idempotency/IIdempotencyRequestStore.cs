namespace ShopInventory.Common.Idempotency;

public interface IIdempotencyRequestStore
{
    Task<IdempotencyAcquireResult<TResponse>> TryAcquireAsync<TResponse>(
        string scope,
        string key,
        object request,
        CancellationToken cancellationToken);

    /// <summary>
    /// As <see cref="TryAcquireAsync{TResponse}(string, string, object, CancellationToken)"/>, but a
    /// claim this acquires lapses after <paramref name="inProgressLease"/> unless
    /// <see cref="RenewAsync"/> keeps it alive.
    /// </summary>
    /// <remarks>
    /// For work whose owner can die holding the claim — an app-pool recycle mid-post — and where
    /// refusing every retry for the full expiry window is the costlier failure. The owner renews while
    /// it runs, so the lease measures how long ago the owner was last alive rather than how long the
    /// work has taken. Null keeps the ordinary expiry. A completed claim keeps the ordinary expiry
    /// either way, since that is how long its result is replayed.
    /// </remarks>
    Task<IdempotencyAcquireResult<TResponse>> TryAcquireAsync<TResponse>(
        string scope,
        string key,
        object request,
        TimeSpan? inProgressLease,
        CancellationToken cancellationToken);

    /// <summary>
    /// Pushes an in-progress claim's expiry out to <paramref name="lease"/> from now.
    /// </summary>
    /// <returns>
    /// False when the claim is no longer this caller's to renew: completed, released, or lapsed and
    /// deleted so that somebody else could acquire it.
    /// </returns>
    Task<bool> RenewAsync(
        long requestId,
        TimeSpan lease,
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

    /// <summary>
    /// Takes over a claim an earlier attempt left in progress and abandoned, so this attempt owns it.
    /// </summary>
    /// <remarks>
    /// The counterpart to keeping a claim whose outcome was unknown. A claim is held open precisely
    /// when we could not tell whether the work committed, and holding it forever would be its own
    /// failure: the caller could never retry, and a post that genuinely never landed would never be
    /// made good. So once the caller has established that the work did not happen — by asking the
    /// system of record, not by waiting — the stale claim is taken over rather than deleted and
    /// re-acquired, which would open a window for a third attempt to slip in between the two.
    ///
    /// <para>Atomic and conditional: it succeeds for exactly one caller, and only while the claim is
    /// still in progress and older than <paramref name="issuedBeforeUtc"/>. A claim another attempt
    /// has since taken over, or completed, is left alone and false is returned.</para>
    /// </remarks>
    /// <returns>True when this caller now owns the claim.</returns>
    Task<bool> TryTakeOverAsync(
        long requestId,
        DateTime issuedBeforeUtc,
        CancellationToken cancellationToken);
}
