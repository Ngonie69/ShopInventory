using ShopInventory.Services;

namespace ShopInventory.Middleware;

/// <summary>
/// Keeps the number of <c>STK_ITEMS</c> stock reads the Service Layer works on at once at or below
/// <c>SAP:StockItemsMaxConcurrentReads</c>.
/// </summary>
/// <remarks>
/// <para>
/// Measured on KFLDB on 2026-11-11 from the Service Layer's access logs: of the 101
/// <c>SQLQueries('STK_ITEMS_…')/List</c> reads that took over a minute, 61 began while three or more
/// others of the same family were already running there (35 of 41 after 11:50). A slow read is how a
/// Service Layer process gets stuck at full CPU, its memory cycling up to ~8 GB, for up to two hours;
/// enough of those and Linux kills HANA (twice on 2026-11-11). Every caller reads one warehouse at a
/// time, so the overlap is several callers at once — invoice and reservation stock checks, transfer
/// forms, the Web's stock refresh, the hourly ledger check — and only a limit here, which they all
/// pass through, sees all of them.
/// </para>
/// <para>
/// Inside <see cref="SAPStuckReadHandler"/>, so a held read is refused without waiting for a slot, and
/// outside the circuit breaker and <see cref="SAPConcurrencyHandler"/>: a read queued here holds none
/// of the six process-wide slots, and its stock budget running out while it queues is not counted as
/// SAP failing to answer, because it never reached SAP.
/// </para>
/// <para>
/// Background reads wait for a slot for as long as their budget allows. A read a person is waiting on
/// waits at most <c>SAP:StockItemsInteractiveMaxWaitSeconds</c> and is then sent anyway: the invoice
/// stock guard fails closed, so leaving it queued behind stuck reads until its budget ran out would
/// refuse the invoice.
/// </para>
/// </remarks>
public sealed class SAPStockItemsReadHandler(
    SapStockItemsReadGate gate,
    TimeProvider timeProvider,
    ILogger<SAPStockItemsReadHandler> logger) : DelegatingHandler
{
    /// <summary>Waits shorter than this are normal queueing and are not logged.</summary>
    private static readonly TimeSpan WorthLogging = TimeSpan.FromSeconds(2);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!gate.Enabled || !IsStockItemsRead(request))
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var interactive = SapRequestPriority.IsInteractive;
        var path = request.RequestUri!.AbsolutePath;
        var freeOnArrival = gate.Available;
        var waitStarted = timeProvider.GetTimestamp();

        var holdsSlot = await gate.EnterAsync(interactive ? gate.InteractiveMaxWait : null, cancellationToken);
        var waited = timeProvider.GetElapsedTime(waitStarted);

        if (!holdsSlot)
        {
            logger.LogWarning(
                "Sending {Path} to SAP without a STK_ITEMS slot: a person is waiting on it, and all {Limit} were still busy after {WaitSeconds:0} s",
                path,
                gate.Limit,
                waited.TotalSeconds);
        }
        else if (waited >= WorthLogging)
        {
            logger.LogWarning(
                "SAP stock read waited {WaitMilliseconds:0}ms for one of the {Limit} STK_ITEMS slots ({Path}, {Priority}); free on arrival {FreeOnArrival}",
                waited.TotalMilliseconds,
                gate.Limit,
                path,
                interactive ? "interactive" : "background",
                freeOnArrival);
        }

        try
        {
            return await base.SendAsync(request, cancellationToken);
        }
        finally
        {
            if (holdsSlot)
            {
                gate.Exit();
            }
        }
    }

    /// <summary>A GET of a stored <c>STK_ITEMS_*</c> query's rows, the read the limit is for.</summary>
    internal static bool IsStockItemsRead(HttpRequestMessage request) =>
        request.Method == HttpMethod.Get
        && request.RequestUri is { IsAbsoluteUri: true } uri
        && uri.AbsolutePath.Contains("/SQLQueries('STK_ITEMS_", StringComparison.Ordinal)
        && uri.AbsolutePath.EndsWith("')/List", StringComparison.Ordinal);
}
