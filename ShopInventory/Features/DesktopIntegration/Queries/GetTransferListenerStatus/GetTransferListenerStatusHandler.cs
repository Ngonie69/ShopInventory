using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetTransferListenerStatus;

public sealed class GetTransferListenerStatusHandler(
    ITransferEventListenerClient listenerClient,
    IOptions<DailyStockSettings> dailyStockSettings,
    ILogger<GetTransferListenerStatusHandler> logger
) : IRequestHandler<GetTransferListenerStatusQuery, ErrorOr<TransferListenerStatusResult>>
{
    public async Task<ErrorOr<TransferListenerStatusResult>> Handle(
        GetTransferListenerStatusQuery request,
        CancellationToken cancellationToken)
    {
        if (!listenerClient.IsEnabled)
        {
            return Disabled(listenerClient.BaseUrl);
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
            return Unreachable(listenerClient.BaseUrl, ex.Message);
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

        var poll = health.Poll;
        TransferListenerPollSummary? pollSummary = null;

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
                Math.Round((DateTime.UtcNow - reference).TotalMinutes, 1));
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

        var recent = stats.RecentDocuments
            .OrderByDescending(document => document.DetectedAt)
            .Take(Math.Max(request.RecentDocumentCount, 1))
            .Select(document => new TransferListenerDocumentSummary(
                document.SapDocNum,
                document.SapDocDate,
                document.DetectedAt,
                document.Direction,
                document.MonitoredWarehouse,
                document.SourceWarehouse,
                document.DestinationWarehouse,
                document.WebhookSuccess,
                document.LineCount,
                document.Lines
                    .Select(line => new TransferListenerLineSummary(
                        line.ItemCode, line.ItemDescription, line.Quantity))
                    .ToList()))
            .ToList();

        return new TransferListenerStatusResult(
            Enabled: true,
            Reachable: true,
            BaseUrl: listenerClient.BaseUrl,
            UnreachableReason: null,
            Status: health.Status,
            Message: health.Message,
            Poll: pollSummary,
            DocumentsSeen: health.DocumentsSeen,
            LinesSeen: health.LinesSeen,
            InboundDocuments: stats.InboundDocuments,
            OutboundDocuments: stats.OutboundDocuments,
            WebhookSuccessCount: stats.WebhookSuccessCount,
            WebhookFailureCount: stats.WebhookFailureCount,
            WatchedWarehouses: watched,
            UnwatchedWarehouses: unwatched,
            DocumentsByWarehouse: stats.TransfersByWarehouse,
            RecentDocuments: recent);
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

    private static TransferListenerStatusResult Disabled(string baseUrl) => new(
        Enabled: false,
        Reachable: false,
        BaseUrl: baseUrl,
        UnreachableReason: "The TransferEventListener integration is switched off "
            + "(TransferEventListener:Enabled). Its inbound webhook is unaffected and still applies "
            + "transfers to the snapshot; only this API's calls out to it are disabled.",
        Status: null,
        Message: null,
        Poll: null,
        DocumentsSeen: 0,
        LinesSeen: 0,
        InboundDocuments: 0,
        OutboundDocuments: 0,
        WebhookSuccessCount: 0,
        WebhookFailureCount: 0,
        WatchedWarehouses: [],
        UnwatchedWarehouses: [],
        DocumentsByWarehouse: new Dictionary<string, int>(),
        RecentDocuments: []);

    private static TransferListenerStatusResult Unreachable(string baseUrl, string reason) => new(
        Enabled: true,
        Reachable: false,
        BaseUrl: baseUrl,
        UnreachableReason: reason,
        Status: null,
        Message: null,
        Poll: null,
        DocumentsSeen: 0,
        LinesSeen: 0,
        InboundDocuments: 0,
        OutboundDocuments: 0,
        WebhookSuccessCount: 0,
        WebhookFailureCount: 0,
        WatchedWarehouses: [],
        UnwatchedWarehouses: [],
        DocumentsByWarehouse: new Dictionary<string, int>(),
        RecentDocuments: []);
}
