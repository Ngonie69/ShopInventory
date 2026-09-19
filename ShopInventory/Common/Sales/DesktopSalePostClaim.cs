using ShopInventory.Common.Idempotency;

namespace ShopInventory.Common.Sales;

/// <summary>
/// The right to post one sale to SAP, held for as long as the post is in flight.
/// </summary>
/// <remarks>
/// Disposing without completing gives the claim back, so a refused or failed post leaves the sale
/// retryable. That is the reason this is a disposable object rather than a pair of calls: every path
/// out of a posting routine — a SAP rejection, a cancellation, an unhandled exception — has to release,
/// and only <c>await using</c> covers all three without the caller remembering to.
/// </remarks>
public sealed class DesktopSalePostClaim : IAsyncDisposable
{
    private readonly IIdempotencyRequestStore? _store;
    private readonly ILogger? _logger;
    private readonly string _externalReferenceId;
    private readonly long? _requestId;
    private readonly CancellationTokenSource? _heartbeatStop;
    private readonly Task? _heartbeat;
    private bool _completed;
    private bool _heartbeatStopped;

    private DesktopSalePostClaim(
        DesktopSalePostClaimOutcome outcome,
        string externalReferenceId,
        DesktopSalePostReceipt? receipt = null,
        IIdempotencyRequestStore? store = null,
        ILogger? logger = null,
        long? requestId = null,
        TimeSpan? lease = null)
    {
        Outcome = outcome;
        _externalReferenceId = externalReferenceId;
        Receipt = receipt;
        _store = store;
        _logger = logger;
        _requestId = requestId;

        if (store is not null && requestId is not null && lease is { } leaseLength && leaseLength > TimeSpan.Zero)
        {
            _heartbeatStop = new CancellationTokenSource();
            _heartbeat = Task.Run(() => RenewWhileHeldAsync(leaseLength, _heartbeatStop.Token));
        }
    }

    public DesktopSalePostClaimOutcome Outcome { get; }

    /// <summary>The invoice an earlier post left in SAP; set only when <see cref="Outcome"/> is
    /// <see cref="DesktopSalePostClaimOutcome.AlreadyPosted"/>.</summary>
    public DesktopSalePostReceipt? Receipt { get; }

    public bool Granted => Outcome == DesktopSalePostClaimOutcome.Granted;

    internal static DesktopSalePostClaim ForGrant(
        string externalReferenceId,
        long requestId,
        IIdempotencyRequestStore store,
        ILogger logger,
        TimeSpan? lease = null)
        => new(DesktopSalePostClaimOutcome.Granted, externalReferenceId, store: store, logger: logger,
            requestId: requestId, lease: lease);

    internal static DesktopSalePostClaim ForReplay(string externalReferenceId, DesktopSalePostReceipt receipt)
        => new(DesktopSalePostClaimOutcome.AlreadyPosted, externalReferenceId, receipt);

    internal static DesktopSalePostClaim ForInFlight(string externalReferenceId)
        => new(DesktopSalePostClaimOutcome.InFlight, externalReferenceId);

    /// <summary>
    /// A granted claim backed by nothing, for a guard that does not exclude anyone.
    /// </summary>
    /// <remarks>
    /// Exists for the test double that models "nobody else is posting this sale". Completing and
    /// disposing it are both no-ops, which is exactly right for that: there is no store to record
    /// into. Not <c>internal</c>-only by accident — no production path builds one, and one that did
    /// would be a guard that guards nothing.
    /// </remarks>
    public static DesktopSalePostClaim ForUnguarded(string externalReferenceId)
        => new(DesktopSalePostClaimOutcome.Granted, externalReferenceId);

    /// <summary>
    /// Records that SAP holds this invoice, so a later attempt is answered with the document instead
    /// of being allowed to post a second one.
    /// </summary>
    /// <remarks>
    /// Call this only once the document numbers are durable on the sale. The claim is a guard against
    /// a concurrent post, not a substitute for the row.
    /// </remarks>
    public async Task CompleteAsync(int sapDocEntry, int sapDocNum)
    {
        if (_completed || _store is null || _requestId is null)
        {
            return;
        }

        _completed = true;

        // Stopped first: a renewal landing after the completion would find no in-progress row and
        // report the claim lost when it was in fact finished.
        await StopRenewingAsync();

        // CancellationToken.None: the invoice exists in SAP by now, and a caller that hung up must
        // not be the reason the next attempt is allowed to raise a second one.
        await _store.CompleteAsync(
            _requestId.Value,
            new DesktopSalePostReceipt(sapDocEntry, sapDocNum),
            CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await StopRenewingAsync();

        if (_completed || _store is null || _requestId is null)
        {
            return;
        }

        try
        {
            // Never the caller's token — the commonest reason a post did not complete is that the
            // request was cancelled, which is exactly when that token would cancel the release too
            // and leave the sale unpostable for the whole expiry window.
            await _store.ReleaseAsync(_requestId.Value, CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(
                exception,
                "Failed to release the SAP post claim for sale {ExternalReference}. It expires on its own.",
                _externalReferenceId);
        }
    }

    /// <summary>
    /// Keeps the claim alive for as long as this object is, so that only a claim whose owner is gone
    /// lapses.
    /// </summary>
    /// <remarks>
    /// Renews at a quarter of the lease, so three renewals can fail in a row — a database blip, a
    /// starved thread pool — before a live post loses its claim. A post hung inside SAP keeps renewing,
    /// deliberately: its invoice may yet commit, and it is the one case where letting a second post in
    /// would be wrong.
    /// </remarks>
    private async Task RenewWhileHeldAsync(TimeSpan lease, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(lease / 4);

        try
        {
            while (await timer.WaitForNextTickAsync(stop))
            {
                try
                {
                    // Never the stop token: a renewal cut off half way is indistinguishable from a
                    // failed one, and stopping only needs the loop to end, which the timer sees.
                    if (!await _store!.RenewAsync(_requestId!.Value, lease, CancellationToken.None))
                    {
                        _logger?.LogError(
                            "The SAP post claim for sale {ExternalReference} lapsed while its post was still running; "
                            + "another post may now be claimed for it.",
                            _externalReferenceId);
                        return;
                    }
                }
                catch (Exception exception)
                {
                    _logger?.LogWarning(
                        exception,
                        "Failed to renew the SAP post claim for sale {ExternalReference}; retrying.",
                        _externalReferenceId);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }

    private async Task StopRenewingAsync()
    {
        if (_heartbeatStop is null || _heartbeatStopped)
        {
            return;
        }

        _heartbeatStopped = true;
        _heartbeatStop.Cancel();

        // Awaited so a renewal already under way finishes before the claim is completed or released.
        await _heartbeat!;
        _heartbeatStop.Dispose();
    }
}
