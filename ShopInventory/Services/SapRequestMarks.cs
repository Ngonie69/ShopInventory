namespace ShopInventory.Services;

/// <summary>
/// What the SAP pipeline writes on an outgoing request so <c>SAPCircuitBreakerHandler</c> can tell
/// SAP failing to answer from a caller that stopped waiting.
/// </summary>
/// <remarks>
/// <para>
/// Both end the same way inside the handler: the token it was given is cancelled. A caller that
/// gave up cancels it, and so does every timeout — <see cref="HttpClient.Timeout"/> cancels a
/// source it links to the caller's token, and a per-call budget is a linked source of the same
/// kind. HttpClient only turns its own timeout into a <see cref="TimeoutException"/> after the
/// handler chain has unwound, and a budget only becomes one in the method that set it. So the
/// breaker never saw a timeout, and never opened for the failure that hurt most: SAP accepting a
/// request and not answering, while the caller held one of the concurrency slots for its whole
/// budget.
/// </para>
/// <para>
/// A mark on the request rather than a wrapper type, because the exception each caller sees must
/// not change: the transient retry and <see cref="SapFailureClassifier.DefinitelyNotCommitted"/>
/// both decide by it.
/// </para>
/// </remarks>
public static class SapRequestMarks
{
    private static readonly HttpRequestOptionsKey<bool> ReachedSapKey = new("ShopInventory.Sap.ReachedSap");
    private static readonly HttpRequestOptionsKey<CancellationToken> BreakerDeadlineKey =
        new("ShopInventory.Sap.BreakerDeadline");

    /// <summary>
    /// Records that the request holds a concurrency slot and is on its way to SAP.
    /// </summary>
    /// <remarks>
    /// Set by <c>SAPConcurrencyHandler</c>. A request that timed out still queued for a slot never
    /// reached SAP, so it says nothing about whether SAP is answering — the requests holding the
    /// slots do, and they count for themselves.
    /// </remarks>
    public static void MarkReachedSap(HttpRequestMessage request) =>
        request.Options.Set(ReachedSapKey, true);

    public static bool ReachedSap(HttpRequestMessage request) =>
        request.Options.TryGetValue(ReachedSapKey, out var reached) && reached;

    /// <summary>
    /// Attaches a deadline whose expiry the breaker should count as SAP failing to answer.
    /// </summary>
    /// <param name="request">The request being sent.</param>
    /// <param name="deadline">
    /// A token cancelled by the deadline alone — not linked to the caller's token — so a caller
    /// giving up can never read as the deadline passing.
    /// </param>
    /// <remarks>
    /// Only for a deadline that means "SAP is not answering". A budget that exists to bound a path
    /// with a fallback of its own must not set it; see the price-list budget in
    /// <c>SAPServiceLayerClient</c>.
    /// </remarks>
    public static void SetBreakerDeadline(HttpRequestMessage request, CancellationToken deadline) =>
        request.Options.Set(BreakerDeadlineKey, deadline);

    public static bool BreakerDeadlineExpired(HttpRequestMessage request) =>
        request.Options.TryGetValue(BreakerDeadlineKey, out var deadline) && deadline.IsCancellationRequested;
}
