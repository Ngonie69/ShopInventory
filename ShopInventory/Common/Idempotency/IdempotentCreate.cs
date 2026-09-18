using ErrorOr;
using IdempotencyErrors = ShopInventory.Common.Errors.Errors.Idempotency;

namespace ShopInventory.Common.Idempotency;

/// <summary>
/// Runs a document create under an <see cref="IIdempotencyRequestStore"/> claim, for handlers whose
/// document carries nothing SAP could be asked about afterwards.
/// </summary>
/// <remarks>
/// The purchase documents and transfer requests reach SAP with no reference of ours on them, so a
/// post whose reply was lost cannot be looked up and adopted the way credit notes and invoices are.
/// What stops the retry that follows from raising a second document is the claim alone:
///
/// <list type="bullet">
/// <item>A completed claim replays the document to any retry under the same key.</item>
/// <item>A failure that proves nothing was created gives the claim back, so the caller can fix the
/// document and send it again under the same key.</item>
/// <item>Any other failure — a timeout, a dropped connection, a reply that could not be read — keeps
/// the claim. The document may exist, and with nothing to ask SAP there is no safe moment to send
/// again: the retry is refused until the claim expires
/// (<c>SecuritySettings.IdempotencyKeyExpirationMinutes</c>) and told to check SAP. That is the safe
/// direction; the other one is a second purchase invoice or transfer request.</item>
/// </list>
///
/// The create receives the caller's token and owns its own commit point: it must stop honouring the
/// token once it starts sending, or a closed tab aborts the post mid-flight.
/// </remarks>
public static class IdempotentCreate
{
    /// <summary>
    /// Runs <paramref name="create"/>, under a claim when <paramref name="clientRequestId"/> is given.
    /// </summary>
    /// <param name="store">Where the claim is held.</param>
    /// <param name="logger">The calling handler's logger.</param>
    /// <param name="scope">The store scope, e.g. "purchaseinvoices.create".</param>
    /// <param name="operation">How the document is named in messages, e.g. "purchase invoice creation".</param>
    /// <param name="clientRequestId">The caller's key; without one the create runs unguarded, as it always did.</param>
    /// <param name="request">What the key was issued for; a retry carrying a different payload is refused.</param>
    /// <param name="create">
    /// The create itself. It returns its failures rather than throwing them, and marks one whose
    /// outcome is unknown with <see cref="IdempotencyErrors.OutcomeUnknown"/>. An exception that
    /// escapes it is treated as unknown, except a cancellation of <paramref name="cancellationToken"/>,
    /// which can only happen before the commit point.
    /// </param>
    /// <param name="cancellationToken">
    /// The caller's token, handed to <paramref name="create"/>; the completion and the release never
    /// run on it.
    /// </param>
    public static async Task<ErrorOr<TResponse>> RunAsync<TResponse>(
        IIdempotencyRequestStore store,
        ILogger logger,
        string scope,
        string operation,
        string? clientRequestId,
        object request,
        Func<CancellationToken, Task<ErrorOr<TResponse>>> create,
        CancellationToken cancellationToken)
    {
        var key = string.IsNullOrWhiteSpace(clientRequestId) ? null : clientRequestId.Trim();
        if (key is null)
        {
            return await create(cancellationToken);
        }

        var acquired = await store.TryAcquireAsync<TResponse>(scope, key, request, cancellationToken);
        switch (acquired.Outcome)
        {
            case IdempotencyAcquireOutcome.ReplayAvailable when acquired.Response is not null:
                logger.LogWarning("Replaying {Operation} for idempotency key {Key}", operation, key);
                return acquired.Response;
            case IdempotencyAcquireOutcome.RequestMismatch:
                return IdempotencyErrors.RequestMismatch(operation);
            case IdempotencyAcquireOutcome.Acquired when acquired.RequestId.HasValue:
                break;
            default:
                // Either another attempt is running right now, or an earlier one reached SAP and
                // never learned what became of it. Neither can be told apart from here, and both
                // mean the same thing to the caller: do not send it again.
                return IdempotencyErrors.PostOutcomeUnconfirmed(operation);
        }

        var requestId = acquired.RequestId.Value;
        var release = true;
        try
        {
            var result = await create(cancellationToken);

            if (!result.IsError)
            {
                try
                {
                    await store.CompleteAsync(requestId, result.Value, CancellationToken.None);
                }
                catch (Exception completeException)
                {
                    // The document exists either way. Keeping the claim leaves retries refused
                    // until it expires rather than letting one through to post it twice.
                    logger.LogWarning(
                        completeException,
                        "Failed to persist {Operation} idempotency completion for request {RequestId}",
                        operation,
                        requestId);
                }

                release = false;
                return result;
            }

            if (result.Errors.Any(IdempotencyErrors.IsOutcomeUnknown))
            {
                release = false;
                logger.LogError(
                    "{Operation} under key {Key} may have reached SAP without an answer; the claim is kept so a retry cannot post it twice",
                    operation,
                    key);
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up before the create reached its commit point — past it, the create
            // stops honouring this token — so nothing was sent. The claim goes back, and the throw
            // carries on to become the 499 it always was.
            throw;
        }
        catch (Exception ex)
        {
            release = false;
            logger.LogError(ex, "{Operation} under key {Key} failed with its outcome unknown; the claim is kept", operation, key);
            return IdempotencyErrors.PostOutcomeUnconfirmed(operation);
        }
        finally
        {
            if (release)
            {
                try
                {
                    await store.ReleaseAsync(requestId, CancellationToken.None);
                }
                catch (Exception releaseException)
                {
                    logger.LogWarning(
                        releaseException,
                        "Failed to release {Operation} idempotency request {RequestId}",
                        operation,
                        requestId);
                }
            }
        }
    }
}
