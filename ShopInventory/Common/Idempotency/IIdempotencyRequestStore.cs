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
