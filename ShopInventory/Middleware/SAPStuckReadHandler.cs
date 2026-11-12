using System.Net;
using ShopInventory.Services;

namespace ShopInventory.Middleware;

/// <summary>
/// Keeps background work from sending SAP a SQLQueries read that SAP is presumed to be still working
/// on. Why, and the measurements: <see cref="SapStuckReadState"/>.
/// </summary>
/// <remarks>
/// <para>
/// Outermost in the SAP pipeline. A held read is refused before it queues for a concurrency slot, and
/// before the circuit breaker, which would otherwise count the refusal as SAP failing to answer.
/// </para>
/// <para>
/// A read is presumed still running when it reached SAP (<see cref="SapRequestMarks.MarkReachedSap"/>,
/// set by <see cref="SAPConcurrencyHandler"/>) and ended without SAP's answer once SAP had had it for
/// <see cref="SapStuckReadState.MinimumTimeAtSap"/>. Either it was cancelled — by its budget, by
/// <see cref="HttpClient.Timeout"/> or by its caller, none of which stops the Service Layer — or the
/// load balancer answered 502 or 504, which it does when it stops waiting for the node while the node
/// carries on. The key is the method and the whole URL, so another page or another parameter is a
/// different read.
/// </para>
/// <para>
/// Only background reads are refused. A read a person is waiting on is always sent: the invoice stock
/// guard fails closed (<c>SAPSettings.StockGuardFailClosed</c>), so refusing one would stop invoicing
/// from that warehouse for the whole hold, and a fresh attempt usually lands on a Service Layer process
/// that is not stuck (269 of the 379 measured repeats answered). Such a read still starts a hold when
/// it gets stuck, and SAP answering it ends one.
/// </para>
/// </remarks>
public sealed class SAPStuckReadHandler(
    SapStuckReadState state,
    ILogger<SAPStuckReadHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!state.Enabled || !IsStoredQueryRead(request))
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var key = SapStuckReadState.KeyOf(request);
        var path = request.RequestUri!.AbsolutePath;

        if (!SapRequestPriority.IsInteractive && state.IsHeld(key, out var heldUntil))
        {
            logger.LogInformation(
                "Not sending {Path} to SAP: an identical read got no answer and is presumed to be still running there until {HeldUntil:u}",
                path,
                heldUntil);
            throw new SapReadStillRunningException(path, heldUntil);
        }

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (state.PresumedStuckAfter(request) is { } atSap)
        {
            StartHold(key, path, "gave no answer", atSap);
            throw;
        }

        if (response.IsSuccessStatusCode)
        {
            state.Release(key);
        }
        else if (response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout
                 && state.PresumedStuckAfter(request) is { } atSap)
        {
            StartHold(key, path, $"answered {(int)response.StatusCode}", atSap);
        }

        return response;
    }

    private static bool IsStoredQueryRead(HttpRequestMessage request) =>
        request.Method == HttpMethod.Get
        && request.RequestUri is { IsAbsoluteUri: true } uri
        && uri.AbsolutePath.Contains("/SQLQueries(", StringComparison.Ordinal)
        && uri.AbsolutePath.EndsWith("/List", StringComparison.Ordinal);

    private void StartHold(string key, string path, string outcome, TimeSpan atSap)
    {
        var until = state.Hold(key);
        logger.LogWarning(
            "SAP {Outcome} to {Path} after {SecondsAtSap:0} s and is presumed to be still working on it; background reads of the same URL are held until {HeldUntil:u}",
            outcome,
            path,
            atSap.TotalSeconds,
            until);
    }
}
