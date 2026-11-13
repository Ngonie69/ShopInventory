using System.Net;
using ShopInventory.Services;
using ShopInventory.Services.CallTimings;

namespace ShopInventory.Middleware;

/// <summary>
/// Times every SAP Service Layer call, SQLQueries reads included, for the Admin call timings page:
/// how long it took, how long it queued for a read-family slot and for a process-wide slot, and how it
/// ended.
/// </summary>
/// <param name="recorder">Where the call is kept.</param>
/// <param name="gate">The read families, so a read is grouped under the family its cap uses.</param>
/// <param name="stuckReads">The stuck-read rule, so a call SAP is presumed to be still working on is marked.</param>
/// <param name="clientTimeout">
/// The <see cref="HttpClient.Timeout"/> of the client this handler sits in. HttpClient's own timeout
/// reaches a handler as a cancelled token, like a caller giving up; a call that ran this long is the
/// timeout.
/// </param>
/// <remarks>
/// <para>
/// Outermost in both SAP clients, outside <see cref="SAPStuckReadHandler"/>, so a read the hold, the
/// family cap or the circuit breaker refuses is recorded as not sent rather than missed. The waits are
/// written onto the call's <see cref="SapCallInFlight"/> by <see cref="SAPReadFamilyHandler"/> and
/// <see cref="SAPConcurrencyHandler"/>.
/// </para>
/// <para>
/// Nothing here changes what the caller sees: the response and every exception pass through as they
/// came. The only work on the call's path is a dictionary insert and remove and one struct copy.
/// </para>
/// </remarks>
public sealed class SAPCallTimingHandler(
    CallTimingRecorder recorder,
    SapReadFamilyGate gate,
    SapStuckReadState stuckReads,
    TimeSpan clientTimeout) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!recorder.Enabled)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var clock = recorder.TimeProvider;
        var (family, operation) = SapCallLabels.Of(request, gate);
        var context = ApiCallContext.Current;
        var call = new SapCallInFlight(
            clock.GetTimestamp(),
            clock.GetUtcNow().UtcDateTime,
            family,
            operation,
            context?.Route ?? ApiCallContext.Background,
            SapRequestPriority.IsInteractive);
        SapCallInFlight.Attach(request, call);
        recorder.Began(call);

        HttpResponseMessage? response = null;
        Exception? failure = null;
        try
        {
            response = await base.SendAsync(request, cancellationToken);
            return response;
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            var elapsed = clock.GetElapsedTime(call.StartedTimestamp);
            var outcome = OutcomeOf(request, response, failure, elapsed, clientTimeout);
            var reachedSap = call.ReachedSapTimestamp;
            var atSap = reachedSap == 0 ? TimeSpan.Zero : clock.GetElapsedTime(reachedSap);
            var presumedStuck = (outcome is SapCallOutcome.Timeout or SapCallOutcome.GaveUp
                                 || response?.StatusCode is HttpStatusCode.BadGateway)
                                && stuckReads.PresumedStuckAfter(request) is not null;

            var record = new SapCallRecord(
                call.StartedUtc.Ticks,
                Milliseconds(elapsed),
                Milliseconds(call.FamilyWait ?? TimeSpan.Zero),
                Milliseconds(call.SlotWait ?? TimeSpan.Zero),
                Milliseconds(atSap),
                call.Family,
                call.Operation,
                call.Origin,
                outcome,
                (short)(response is null ? 0 : (int)response.StatusCode),
                call.Interactive,
                presumedStuck);

            recorder.Finished(call, record);
            context?.AddSapCall(record.DurationMs);
        }
    }

    /// <summary>How a call ended; see <see cref="SapCallOutcome"/> for what each means.</summary>
    internal static SapCallOutcome OutcomeOf(
        HttpRequestMessage request,
        HttpResponseMessage? response,
        Exception? failure,
        TimeSpan elapsed,
        TimeSpan clientTimeout)
    {
        if (response is not null)
        {
            return response.IsSuccessStatusCode ? SapCallOutcome.Ok
                : response.StatusCode == HttpStatusCode.GatewayTimeout ? SapCallOutcome.Timeout
                : SapCallOutcome.Error;
        }

        return failure switch
        {
            SapReadNotSentException or SapCircuitOpenException => SapCallOutcome.NotSent,
            OperationCanceledException when elapsed >= clientTimeout || SapRequestMarks.BreakerDeadlineExpired(request)
                => SapCallOutcome.Timeout,
            OperationCanceledException => SapCallOutcome.GaveUp,
            TimeoutException => SapCallOutcome.Timeout,
            _ => SapCallOutcome.Error,
        };
    }

    private static int Milliseconds(TimeSpan value) =>
        (int)Math.Clamp(Math.Round(value.TotalMilliseconds), 0, int.MaxValue);
}
