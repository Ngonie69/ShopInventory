using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Stock;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetTransferListenerStatus;

public sealed class GetTransferListenerStatusHandler(
    ITransferEventListenerClient listenerClient,
    ApplicationDbContext context,
    IOptions<DailyStockSettings> dailyStockSettings,
    ILogger<GetTransferListenerStatusHandler> logger
) : IRequestHandler<GetTransferListenerStatusQuery, ErrorOr<TransferListenerStatusResult>>
{
    public const string Applied = "Applied";
    public const string Waiting = "Waiting";
    public const string NotApplied = "NotApplied";

    public async Task<ErrorOr<TransferListenerStatusResult>> Handle(
        GetTransferListenerStatusQuery request,
        CancellationToken cancellationToken)
    {
        // First and regardless of the listener: the ledger is this API's own table, and "nothing has
        // been applied since yesterday" is worth showing most when the listener cannot be asked why.
        var ledger = await ReadLedgerAsync(cancellationToken);

        if (!listenerClient.IsEnabled)
        {
            return Disabled(listenerClient.BaseUrl, ledger);
        }

        // Health first and on its own: it is the one call whose failure means the whole page has
        // nothing to say, and the others are not worth attempting once it has failed.
        TransferListenerHealthDto health;
        try
        {
            health = await listenerClient.GetHealthAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "TransferEventListener at {BaseUrl} could not be read", listenerClient.BaseUrl);

            // Not an error result: "unreachable" is the answer, and the page exists to show it.
            return Unreachable(listenerClient.BaseUrl, ex.Message, ledger);
        }

        var stats = await ReadOrDefaultAsync(
            () => listenerClient.GetStatsAsync(cancellationToken),
            new TransferListenerStatsDto(),
            "statistics",
            cancellationToken);

        var watched = await ReadOrDefaultAsync(
            () => listenerClient.GetMonitoredWarehousesAsync(cancellationToken),
            (IReadOnlyList<string>)[],
            "monitored warehouses",
            cancellationToken);

        var now = DateTime.UtcNow;
        var poll = health.Poll;
        TransferListenerPollSummary? pollSummary = null;
        TransferListenerDeliverySummary? delivery = null;

        if (poll is not null)
        {
            var reference = poll.LastSuccessUtc ?? poll.ProcessStartedUtc;

            pollSummary = new TransferListenerPollSummary(
                poll.ProcessStartedUtc,
                poll.PollingStarted,
                poll.LastAttemptUtc,
                poll.LastSuccessUtc,
                poll.PollingSinceUtc,
                poll.ConsecutiveFailures,
                poll.LastError,
                poll.LastErrorUtc,
                poll.PollIntervalSeconds,
                Math.Round((now - reference).TotalMinutes, 1),
                poll.ResumedFromSavedState,
                poll.ProcessedDocuments);

            delivery = new TransferListenerDeliverySummary(
                poll.PendingNotifications,
                poll.OldestPendingNotificationUtc,
                poll.PendingNotifications > 0 && poll.OldestPendingNotificationUtc is { } oldest
                    ? Math.Round((now - AsUtc(oldest)).TotalMinutes, 1)
                    : null,
                poll.AbandonedNotifications,
                poll.RejectedNotifications,
                poll.WebhookUrl,
                poll.LastDeliveredUtc,
                poll.LastDeliveryError,
                poll.LastDeliveryErrorUtc);
        }

        var watchedSet = watched.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The two warehouse lists are maintained independently — one in this API's configuration, the
        // other compiled into the listener — so they drift, and nothing else compares them.
        //
        // Distinct because the configured list is not guaranteed to be one: binding a section over a
        // property whose initializer already holds items appends rather than replaces, so
        // MonitoredWarehouses arrives holding every warehouse twice. Reporting a warehouse twice
        // would make the reader doubt the page rather than the list.
        var unwatched = dailyStockSettings.Value.MonitoredWarehouses
            .Where(warehouse => !string.IsNullOrWhiteSpace(warehouse))
            .Select(warehouse => warehouse.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(warehouse => watchedSet.Count > 0 && !watchedSet.Contains(warehouse))
            .OrderBy(warehouse => warehouse, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var recentDocuments = stats.RecentDocuments
            .OrderByDescending(document => document.DetectedAt)
            .Take(Math.Max(request.RecentDocumentCount, 1))
            .ToList();

        var appliedAt = await ReadAppliedDocumentsAsync(recentDocuments, cancellationToken);

        var recent = recentDocuments
            .Select(document =>
            {
                DateTime? applied = document.SapDocNum is { } docNum && appliedAt.TryGetValue(docNum, out var at)
                    ? at
                    : null;

                return new TransferListenerDocumentSummary(
                    document.SapDocNum,
                    document.SapDocDate,
                    document.DetectedAt,
                    document.Direction,
                    document.MonitoredWarehouse,
                    document.SourceWarehouse,
                    document.DestinationWarehouse,
                    document.WebhookSuccess,
                    document.Delivery,
                    document.LinesDelivered,
                    document.WebhookResponse,
                    document.LineCount,
                    document.Lines
                        .Select(line => new TransferListenerLineSummary(
                            line.ItemCode, line.ItemDescription, line.Quantity, line.Delivery, line.DeliveryDetail))
                        .ToList(),
                    LocalStockState(document.DetectedAt, applied.HasValue, document.Delivery, delivery),
                    applied);
            })
            .ToList();

        return new TransferListenerStatusResult(
            Enabled: true,
            Reachable: true,
            BaseUrl: listenerClient.BaseUrl,
            UnreachableReason: null,
            Status: health.Status,
            Message: health.Message,
            Poll: pollSummary,
            Delivery: delivery,
            Ledger: ledger,
            DocumentsSeen: health.DocumentsSeen,
            LinesSeen: health.LinesSeen,
            InboundDocuments: stats.InboundDocuments,
            OutboundDocuments: stats.OutboundDocuments,
            WebhookSuccessCount: stats.WebhookSuccessCount,
            WebhookFailureCount: stats.WebhookFailureCount,
            RetryingDocuments: stats.RetryingDocuments,
            WatchedWarehouses: watched,
            UnwatchedWarehouses: unwatched,
            DocumentsByWarehouse: stats.TransfersByWarehouse,
            RecentDocuments: recent);
    }

    /// <summary>
    /// Where a document the listener reports stands against this API's ledger.
    /// </summary>
    /// <remarks>
    /// <para>Applied means the ledger holds an adjustment for the document, and is decided here: the
    /// listener can only say this API took a line, and a line for a warehouse with no snapshot today
    /// is taken and moves nothing.</para>
    ///
    /// <para>Otherwise the listener's per-document delivery says whether the document is still on its
    /// way. Retrying or not yet sent is Waiting; delivered, refused or given up on is NotApplied — the
    /// page tells those apart from the same field.</para>
    ///
    /// <para>A listener that still sent sync-batches reports no delivery per document; its only flag
    /// was that call, which says nothing about the ledger. For one of those the document is Waiting
    /// when the listener holds undelivered lines at least as old as it — the queue is replayed oldest
    /// first, so a document detected after the oldest waiting line has not been confirmed
    /// delivered.</para>
    /// </remarks>
    internal static string LocalStockState(
        DateTime detectedAtUtc,
        bool applied,
        string? listenerDelivery,
        TransferListenerDeliverySummary? delivery)
    {
        if (applied)
        {
            return Applied;
        }

        switch (listenerDelivery)
        {
            case TransferListenerDelivery.Retrying or TransferListenerDelivery.NotSent:
                return Waiting;

            case TransferListenerDelivery.Delivered
                or TransferListenerDelivery.Rejected
                or TransferListenerDelivery.Abandoned:
                return NotApplied;
        }

        return delivery is { PendingLines: > 0, OldestPendingUtc: { } oldest }
               && AsUtc(oldest) <= AsUtc(detectedAtUtc).AddSeconds(1)
            ? Waiting
            : NotApplied;
    }

    private async Task<TransferListenerLedgerSummary> ReadLedgerAsync(CancellationToken cancellationToken)
    {
        var today = StockLedgerDay.Today(dailyStockSettings.Value.StockFetchTimeCAT);

        try
        {
            var rows = await context.StockTransferAdjustments
                .AsNoTracking()
                .Where(adjustment => adjustment.SnapshotDate == today)
                .Select(adjustment => new
                {
                    adjustment.WarehouseCode,
                    adjustment.TransferDocEntry,
                    adjustment.TransferDocNum
                })
                .ToListAsync(cancellationToken);

            // By Id rather than DetectedAt: the key is indexed and rows are only ever appended.
            var last = await context.StockTransferAdjustments
                .AsNoTracking()
                .OrderByDescending(adjustment => adjustment.Id)
                .Select(adjustment => new
                {
                    adjustment.DetectedAt,
                    adjustment.TransferDocNum,
                    adjustment.WarehouseCode
                })
                .FirstOrDefaultAsync(cancellationToken);

            var byWarehouse = rows
                .GroupBy(row => row.WarehouseCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(row => row.TransferDocEntry ?? row.TransferDocNum).Distinct().Count(),
                    StringComparer.OrdinalIgnoreCase);

            return new TransferListenerLedgerSummary(
                today,
                rows.Count,
                rows.Select(row => row.TransferDocEntry ?? row.TransferDocNum).Distinct().Count(),
                last is null ? null : AsUtc(last.DetectedAt),
                last?.TransferDocNum,
                last?.WarehouseCode,
                byWarehouse,
                Available: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not read the transfer adjustments for {SnapshotDate:yyyy-MM-dd}", today);

            return new TransferListenerLedgerSummary(
                today, 0, 0, null, null, null, new Dictionary<string, int>(), Available: false);
        }
    }

    /// <summary>
    /// When the ledger first recorded each of the listed documents, keyed by document number.
    /// </summary>
    /// <remarks>
    /// Keyed by number because that is all the listener's summary carries. Bounded to a day before the
    /// oldest document so the read stays on recent rows however long the table grows.
    /// </remarks>
    private async Task<Dictionary<int, DateTime>> ReadAppliedDocumentsAsync(
        IReadOnlyList<TransferListenerDocumentDto> documents,
        CancellationToken cancellationToken)
    {
        var docNums = documents
            .Where(document => document.SapDocNum.HasValue)
            .Select(document => document.SapDocNum!.Value)
            .Distinct()
            .ToList();

        if (docNums.Count == 0)
        {
            return [];
        }

        // Kind pinned to UTC: DetectedAt is timestamptz, which Npgsql refuses to compare with an
        // Unspecified parameter — and the catch below would turn that into every document "not applied".
        var since = AsUtc(documents.Min(document => document.DetectedAt)).AddDays(-1);

        try
        {
            var rows = await context.StockTransferAdjustments
                .AsNoTracking()
                .Where(adjustment => adjustment.TransferDocNum.HasValue
                                     && docNums.Contains(adjustment.TransferDocNum.Value)
                                     && adjustment.DetectedAt >= since)
                .Select(adjustment => new { DocNum = adjustment.TransferDocNum!.Value, adjustment.DetectedAt })
                .ToListAsync(cancellationToken);

            return rows
                .GroupBy(row => row.DocNum)
                .ToDictionary(group => group.Key, group => AsUtc(group.Min(row => row.DetectedAt)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not match the listener's recent documents against the ledger");
            return [];
        }
    }

    /// <summary>
    /// Runs a secondary read, falling back to an empty value rather than failing the whole query.
    /// </summary>
    /// <remarks>
    /// Health has already answered by the time these run, so the listener is up: a failure here is a
    /// detail missing from the page, not a reason to replace the page with an error. The empty value
    /// is chosen so a caller cannot mistake it for a populated one — an empty warehouse list
    /// suppresses the drift comparison rather than reporting every warehouse as unwatched.
    /// </remarks>
    private async Task<T> ReadOrDefaultAsync<T>(
        Func<Task<T>> read,
        T fallback,
        string what,
        CancellationToken cancellationToken)
    {
        try
        {
            return await read();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not read TransferEventListener {What}", what);
            return fallback;
        }
    }

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static TransferListenerStatusResult Disabled(string baseUrl, TransferListenerLedgerSummary ledger) => new(
        Enabled: false,
        Reachable: false,
        BaseUrl: baseUrl,
        UnreachableReason: "The TransferEventListener integration is switched off "
            + "(TransferEventListener:Enabled). Its inbound webhook is unaffected and still applies "
            + "transfers to the snapshot; only this API's calls out to it are disabled.",
        Status: null,
        Message: null,
        Poll: null,
        Delivery: null,
        Ledger: ledger,
        DocumentsSeen: 0,
        LinesSeen: 0,
        InboundDocuments: 0,
        OutboundDocuments: 0,
        WebhookSuccessCount: 0,
        WebhookFailureCount: 0,
        RetryingDocuments: 0,
        WatchedWarehouses: [],
        UnwatchedWarehouses: [],
        DocumentsByWarehouse: new Dictionary<string, int>(),
        RecentDocuments: []);

    private static TransferListenerStatusResult Unreachable(
        string baseUrl, string reason, TransferListenerLedgerSummary ledger) => new(
        Enabled: true,
        Reachable: false,
        BaseUrl: baseUrl,
        UnreachableReason: reason,
        Status: null,
        Message: null,
        Poll: null,
        Delivery: null,
        Ledger: ledger,
        DocumentsSeen: 0,
        LinesSeen: 0,
        InboundDocuments: 0,
        OutboundDocuments: 0,
        WebhookSuccessCount: 0,
        WebhookFailureCount: 0,
        RetryingDocuments: 0,
        WatchedWarehouses: [],
        UnwatchedWarehouses: [],
        DocumentsByWarehouse: new Dictionary<string, int>(),
        RecentDocuments: []);
}
