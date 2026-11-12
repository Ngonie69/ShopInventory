using System.Net;
using ShopInventory.Services;

namespace ShopInventory.Middleware;

/// <summary>
/// Keeps the number of reads of each <c>SQLQueries</c> read family in <c>SAP:ReadFamilies</c>
/// (<c>STK_ITEMS</c>, <c>WHS_BATCHES</c>, <c>DSK_INV_COST_RANGE</c>, <c>STOCK_QTY</c>) that the Service
/// Layer works on at once at or below the family's limit (2), counting reads it is presumed to be still
/// working on after this client gave up.
/// </summary>
/// <remarks>
/// <para>
/// A slow read of these families is how a Service Layer process gets stuck at full CPU, its memory
/// cycling up to ~8 GB, for up to hours; enough of those and Linux kills HANA (twice on 2026-11-11). On
/// 2026-11-11, 61 of the 101 <c>STK_ITEMS</c> reads that took over a minute began while three or more
/// others were already running on the Service Layer, by its own access logs, which include reads every
/// client had given up on. On 2026-11-12 (06:51–11:10, this API's log, with the stuck-read hold and a
/// three-read <c>STK_ITEMS</c>-only limit both live) 82 reads got stuck, 42 of them
/// <c>WHS_BATCHES_VAN*</c>, and 80 began with <em>no</em> other stuck read of their family in flight
/// from this API: each was sent after this API had given up on the one before, while SAP carried on.
/// A limit on requests in flight alone would have refused one of them.
/// </para>
/// <para>
/// So a read SAP is presumed to be still working on — the same rule as
/// <see cref="SAPStuckReadHandler"/>: it reached SAP, SAP had it at least
/// <see cref="SapStuckReadState.MinimumTimeAtSap"/>, and it was cancelled or answered 502/504 — keeps its
/// slot for the stuck-read hold (<c>SAP:StuckReadHoldMinutes</c>), or until SAP answers the same URL.
/// Replayed over that morning with a limit of two, this refuses 16 of the 81 stuck reads in these
/// families.
/// </para>
/// <para>
/// When the family is full, background work waits at most <c>SAP:ReadFamilyBackgroundMaxWaitSeconds</c>
/// and is refused with <see cref="SapReadFamilyFullException"/>, a timeout its caller already handles.
/// A read a person is waiting on waits at most <c>SAP:ReadFamilyInteractiveMaxWaitSeconds</c>; then a
/// <c>STK_ITEMS</c> read is sent anyway, because the invoice stock guard reads it and fails closed, and
/// any other family's read is refused. Nobody waits when every slot is kept for a stuck read: none
/// frees within seconds.
/// </para>
/// <para>
/// Inside <see cref="SAPStuckReadHandler"/>, so a held read is refused without waiting for a slot, and
/// outside the circuit breaker and <see cref="SAPConcurrencyHandler"/>: a read queued here holds none of
/// the six process-wide slots, and a refusal here is not counted as SAP failing, because the read never
/// reached SAP.
/// </para>
/// </remarks>
public sealed class SAPReadFamilyHandler(
    SapReadFamilyGate gate,
    SapStuckReadState stuckReads,
    TimeProvider timeProvider,
    ILogger<SAPReadFamilyHandler> logger) : DelegatingHandler
{
    /// <summary>Waits shorter than this are normal queueing and are not logged.</summary>
    private static readonly TimeSpan WorthLogging = TimeSpan.FromSeconds(2);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!gate.Enabled
            || SapReadFamilyGate.QueryCodeOf(request) is not { } queryCode
            || gate.FamilyOf(queryCode) is not { } family)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var interactive = SapRequestPriority.IsInteractive;
        var priority = interactive ? "interactive" : "background";
        var path = request.RequestUri!.AbsolutePath;
        var key = SapStuckReadState.KeyOf(request);
        var freeOnArrival = family.Available;
        var maxWait = family.AllKept ? TimeSpan.Zero : interactive ? gate.InteractiveMaxWait : gate.BackgroundMaxWait;
        var waitStarted = timeProvider.GetTimestamp();

        var holdsSlot = await family.EnterAsync(maxWait, cancellationToken);
        var waited = timeProvider.GetElapsedTime(waitStarted);

        if (!holdsSlot)
        {
            var kept = family.Kept;
            if (!(interactive && family.SendInteractiveWhenFull))
            {
                logger.LogWarning(
                    "Not sending {Path} to SAP: all {Limit} {Family} slots are taken, {Kept} by reads SAP is presumed to be still working on ({Priority}, waited {WaitSeconds:0} s)",
                    path,
                    family.Limit,
                    family.Name,
                    kept,
                    priority,
                    waited.TotalSeconds);
                throw new SapReadFamilyFullException(family.Name, family.Limit, path, family.FirstKeptSlotFreesAt);
            }

            logger.LogWarning(
                "Sending {Path} to SAP without a {Family} slot: a person is waiting on it, and all {Limit} were still taken after {WaitSeconds:0} s, {Kept} by reads SAP is presumed to be still working on",
                path,
                family.Name,
                family.Limit,
                waited.TotalSeconds,
                kept);
        }
        else if (waited >= WorthLogging)
        {
            logger.LogWarning(
                "SAP read waited {WaitMilliseconds:0}ms for one of the {Limit} {Family} slots ({Path}, {Priority}); free on arrival {FreeOnArrival}",
                waited.TotalMilliseconds,
                family.Limit,
                family.Name,
                path,
                priority,
                freeOnArrival);
        }

        var stuck = false;
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                family.Release(key);
            }
            else if (response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout)
            {
                stuck = stuckReads.PresumedStuckAfter(request) is not null;
            }

            return response;
        }
        catch (OperationCanceledException) when (stuckReads.PresumedStuckAfter(request) is not null)
        {
            stuck = true;
            throw;
        }
        finally
        {
            if (stuck)
            {
                KeepSlot(family, holdsSlot, key, path);
            }
            else if (holdsSlot)
            {
                family.Exit();
            }
        }
    }

    /// <summary>Keeps a slot for a read SAP is presumed to be still working on, for the stuck-read hold.</summary>
    private void KeepSlot(SapReadFamily family, bool holdsSlot, string key, string path)
    {
        if (!stuckReads.Enabled)
        {
            if (holdsSlot)
            {
                family.Exit();
            }

            return;
        }

        // A person's read sent without a slot has none to keep; it takes one only if one has come free.
        if (!holdsSlot && !family.TryEnter())
        {
            return;
        }

        var until = stuckReads.UtcNow + stuckReads.HoldDuration;
        family.KeepUntil(key, until);
        logger.LogInformation(
            "Keeping one of the {Limit} {Family} slots for {Path} until {Until:u}: SAP is presumed to be still working on it ({Kept} kept)",
            family.Limit,
            family.Name,
            path,
            until,
            family.Kept);
    }
}
