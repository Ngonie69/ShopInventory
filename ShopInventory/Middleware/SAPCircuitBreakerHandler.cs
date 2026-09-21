using System.Diagnostics;
using ShopInventory.Services;

namespace ShopInventory.Middleware;

/// <summary>
/// Opens the SAP circuit after consecutive failures, and refuses requests while it is open.
/// </summary>
/// <param name="circuitBreakerState">The process-wide breaker.</param>
/// <param name="logger">Logger.</param>
/// <param name="clientTimeout">
/// The <see cref="HttpClient.Timeout"/> of the client this handler sits in. A handler cannot see
/// its client, and the cancellation HttpClient raises for its own timeout is indistinguishable here
/// from a caller giving up — so the handler is told the deadline and checks whether it has passed.
/// </param>
/// <remarks>
/// A timeout counts as a failure only once the request reached SAP (see
/// <see cref="SapRequestMarks.MarkReachedSap"/>), so the concurrency handler must sit inside this
/// one. A caller's own cancellation — a closed page, an aborted request, shutdown, a caller-side
/// budget such as a report deadline — never counts.
/// </remarks>
public sealed class SAPCircuitBreakerHandler(
    SapCircuitBreakerState circuitBreakerState,
    ILogger<SAPCircuitBreakerHandler> logger,
    TimeSpan clientTimeout) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (circuitBreakerState.IsSwitchedOff)
        {
            throw new SapCircuitOpenException(
                "The SAP connection is turned off in Settings → SAP Connection. Nothing was sent to SAP.",
                SapCircuitBreakerState.SwitchedOffRetryAfter);
        }

        if (circuitBreakerState.ShouldShortCircuit(out var retryAfter))
        {
            throw new SapCircuitOpenException(
                $"SAP circuit breaker is open. Retry after {Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))} seconds.",
                retryAfter);
        }

        var started = Stopwatch.GetTimestamp();

        try
        {
            var response = await base.SendAsync(request, cancellationToken);

            if (SapFailureClassifier.IsTransientStatusCode(response.StatusCode))
            {
                circuitBreakerState.RecordFailure($"{(int)response.StatusCode} {response.ReasonPhrase}".Trim());
                if (circuitBreakerState.IsOpen)
                {
                    logger.LogWarning("SAP circuit breaker opened after response {StatusCode}", response.StatusCode);
                }
            }
            else
            {
                circuitBreakerState.RecordSuccess();
            }

            return response;
        }
        catch (OperationCanceledException ex) when (
            cancellationToken.IsCancellationRequested && DescribeTimeout(request, started) is { } timeout)
        {
            // SapFailureClassifier.IsTransient reads a cancelled token as the caller giving up, and
            // has to keep doing so: the transient retry must not spend another attempt on a request
            // whose deadline has already gone. Counting it is this handler's decision alone.
            circuitBreakerState.RecordFailure(timeout);
            if (circuitBreakerState.IsOpen)
            {
                logger.LogWarning(ex, "SAP circuit breaker opened after a timeout: {Timeout}", timeout);
            }

            throw;
        }
        catch (Exception ex) when (SapFailureClassifier.IsTransient(ex, cancellationToken))
        {
            circuitBreakerState.RecordFailure(ex.Message);
            if (circuitBreakerState.IsOpen)
            {
                logger.LogWarning(ex, "SAP circuit breaker opened after transient failure");
            }

            throw;
        }
    }

    /// <summary>
    /// Why a cancelled request is a timeout, or null when it was the caller that stopped waiting.
    /// </summary>
    private string? DescribeTimeout(HttpRequestMessage request, long started)
    {
        if (!SapRequestMarks.ReachedSap(request))
        {
            return null;
        }

        if (SapRequestMarks.BreakerDeadlineExpired(request))
        {
            return $"SAP did not answer {request.Method} {request.RequestUri?.AbsolutePath} within its deadline";
        }

        // HttpClient arms its timer just before calling the first handler, so by the time the
        // cancellation reaches here the elapsed time is the timeout less a few microseconds. The
        // tolerance only has to cover that; a caller that cancels inside it was a moment from
        // timing out anyway.
        if (clientTimeout > TimeSpan.Zero && clientTimeout != Timeout.InfiniteTimeSpan)
        {
            var tolerance = TimeSpan.FromTicks(Math.Min(TimeSpan.TicksPerSecond, clientTimeout.Ticks / 20));
            if (Stopwatch.GetElapsedTime(started) >= clientTimeout - tolerance)
            {
                return $"SAP did not answer {request.Method} {request.RequestUri?.AbsolutePath} within the {clientTimeout.TotalSeconds:0.#}s client timeout";
            }
        }

        return null;
    }
}
