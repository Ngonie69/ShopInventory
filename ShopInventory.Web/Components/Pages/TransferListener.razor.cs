using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using ShopInventory.Web.Features.TransferListener.Commands.TriggerTransferListenerCheck;
using ShopInventory.Web.Features.TransferListener.Queries.GetTransferListenerStatus;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

/// <summary>
/// The reading behind /transfer-listener.
/// </summary>
/// <remarks>
/// <para>The page follows a transfer over its three hops — read from SAP, delivered to the API,
/// applied to local stock — and judges each on its own evidence. It used to ask only the first. On
/// 2026-09-17 the listener read SAP every two minutes while posting every line to a port nothing
/// listened on, and the page said "reading SAP normally" beside "33 webhooks delivered, 0 failed" —
/// a figure that counts the listener's batch-sync call, not the ledger. No transfer had reached a till
/// since the previous morning. So the second hop is read from the listener's retry queue and the
/// third from the API's own adjustments table, and the listener's per-document flag is not shown.</para>
///
/// <para>The verdict is computed here rather than taken from the listener's status word, because the
/// listener cannot see the ledger, cannot see which warehouses the API snapshots, and cannot tell "the
/// API could not ask" from "the listener is down".</para>
///
/// <para>There is no auto-refresh. The listener polls SAP every two minutes, so a page that re-read
/// every thirty seconds would mostly redraw the same numbers, and the one action on it is a write.</para>
/// </remarks>
public partial class TransferListener
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IAuditService AuditService { get; set; } = default!;
    [Inject] private AuthenticationStateProvider AuthStateProvider { get; set; } = default!;

    /// <summary>
    /// Past this a poll, or a waiting line, is late enough to say so. Matches
    /// <c>TransferEventListenerSettings.PollStalenessWarningMinutes</c> on the API, whose health check
    /// applies the same two thresholds to both — the page and /health/dependencies would disagree
    /// visibly if either moved alone.
    /// </summary>
    private const double WarningMinutes = 20;

    /// <summary>
    /// The point at which late becomes a fault: movements this old are still not on any till.
    /// </summary>
    private const double CriticalMinutes = 60;

    /// <summary>
    /// A document detected this recently may simply not have been delivered yet, so an unapplied one
    /// is not called a gap until it is older.
    /// </summary>
    private const double UnappliedGraceMinutes = 5;

    private bool isLoading = true;
    private bool isRefreshing;
    private bool isChecking;

    private CheckOutcome? checkOutcome;

    private string? loadError;
    private DateTime? loadedAtUtc;
    private TransferListenerStatusModel? status;

    private string currentUsername = "Unknown";
    private string currentUserRole = "User";

    protected override async Task OnInitializedAsync()
    {
        await LoadCurrentUserAsync();
        await LoadAsync();
        isLoading = false;

        await AuditService.LogAsync(
            "ViewTransferListener", currentUsername, currentUserRole, "System", null,
            "Accessed transfer listener status page", "/transfer-listener");
    }

    private async Task LoadCurrentUserAsync()
    {
        var authState = await AuthStateProvider.GetAuthenticationStateAsync();
        currentUsername = authState.User.Identity?.Name ?? "Unknown";
        currentUserRole = authState.User.Claims
            .FirstOrDefault(claim => claim.Type == System.Security.Claims.ClaimTypes.Role)?.Value ?? "User";
    }

    private async Task LoadAsync()
    {
        var result = await Mediator.Send(new GetTransferListenerStatusQuery());

        if (result.IsError)
        {
            status = null;
            loadError = result.FirstError.Description;
        }
        else
        {
            status = result.Value;
            loadError = null;
        }

        loadedAtUtc = DateTime.UtcNow;
    }

    private async Task RefreshAsync()
    {
        isRefreshing = true;
        try
        {
            await LoadAsync();
        }
        finally
        {
            isRefreshing = false;
        }
    }

    /// <summary>
    /// Asks the listener to poll SAP now. A write: it advances the poll window, marks documents
    /// processed, replays waiting lines and delivers anything new.
    /// </summary>
    private async Task CheckNowAsync()
    {
        isChecking = true;
        checkOutcome = null;

        try
        {
            var result = await Mediator.Send(new TriggerTransferListenerCheckCommand());

            checkOutcome = result.IsError
                ? new CheckOutcome(false, "The check did not run", result.FirstError.Description, null)
                : DescribeCheck(result.Value);

            await AuditService.LogAsync(
                "TriggerTransferListenerCheck", currentUsername, currentUserRole, "System", null,
                "Triggered a manual transfer listener SAP check", "/transfer-listener");

            // The check may have applied a backlog, so the figures on screen are already behind it.
            await LoadAsync();
        }
        finally
        {
            isChecking = false;
        }
    }

    private sealed record CheckOutcome(bool Success, string Headline, string? Detail, string? ListenerMessage);

    /// <summary>
    /// Says what the check did to local stock, which is the question behind pressing it. The
    /// listener's own "webhook succeeded" is the batch-sync call and is not repeated.
    /// </summary>
    private static CheckOutcome DescribeCheck(TransferListenerCheckModel check)
    {
        var reportsDelivery = check.NotificationsDelivered + check.NotificationsQueued + check.NotificationsReplayed
            + check.NotificationsRejected + check.NotificationsAbandoned + check.PendingNotifications > 0;

        var found = $"Read {check.TotalSapTransfers} SAP transfer(s); {check.MonitoredEventsDetected} new line(s) "
            + "for monitored warehouses.";

        if (!reportsDelivery)
        {
            return new CheckOutcome(true, found, null, check.Message);
        }

        var parts = new List<string>();
        if (check.MonitoredEventsDetected > 0 || check.NotificationsDelivered > 0)
            parts.Add($"{check.NotificationsDelivered} delivered to local stock");
        if (check.NotificationsReplayed > 0)
            parts.Add($"{check.NotificationsReplayed} earlier line(s) replayed");
        if (check.NotificationsQueued > 0)
            parts.Add($"{check.NotificationsQueued} queued");
        if (check.NotificationsRejected > 0)
            parts.Add($"{check.NotificationsRejected} refused by the API");
        if (check.NotificationsAbandoned > 0)
            parts.Add($"{check.NotificationsAbandoned} given up on");

        var success = check.NotificationsQueued == 0 && check.NotificationsRejected == 0
            && check.PendingNotifications == 0;

        var detail = string.Join(", ", parts);
        detail = detail.Length == 0 ? string.Empty : char.ToUpperInvariant(detail[0]) + detail[1..] + ". ";
        detail += check.PendingNotifications > 0
            ? $"{check.PendingNotifications} line(s) are still waiting to reach local stock."
            : "Nothing is waiting.";

        return new CheckOutcome(success, found, detail, check.Message);
    }

    // ── Readings ────────────────────────────────────────────────────────────

    private TransferListenerPollModel? Poll => status?.Poll;

    private TransferListenerDeliveryModel? Delivery => status?.Delivery;

    private TransferListenerLedgerModel? Ledger => status?.Ledger;

    /// <summary>
    /// Whether the last successful poll is old enough to matter. Measured on the figure the API
    /// computed, which falls back to the listener's process start when no cycle has ever succeeded —
    /// so a listener that has never worked reads as stale rather than as fresh.
    /// </summary>
    private bool IsPollStale => Poll is { } poll && poll.MinutesSinceSuccessfulPoll >= WarningMinutes;

    private bool IsPollBroken =>
        Poll is { } poll && (!poll.PollingStarted || poll.MinutesSinceSuccessfulPoll >= CriticalMinutes);

    private double OldestWaitMinutes => Delivery is { PendingLines: > 0, MinutesOldestPending: { } minutes }
        ? minutes
        : 0;

    private bool IsDeliveryStuck => OldestWaitMinutes >= CriticalMinutes;

    private bool IsDeliveryLate => OldestWaitMinutes >= WarningMinutes;

    /// <summary>
    /// A listener new enough to report where it delivers also reports everything else about delivery,
    /// so the "delivered of seen" arithmetic is only attempted against one.
    /// </summary>
    private bool ReportsDelivery => !string.IsNullOrWhiteSpace(Delivery?.WebhookUrl);

    private int DeliveredLines => status is null || Delivery is null
        ? 0
        : Math.Max(0, status.LinesSeen - Delivery.PendingLines - Delivery.AbandonedLines - Delivery.RejectedLines);

    /// <summary>
    /// Documents from today's ledger day that reached the API — nothing is waiting in front of them —
    /// and moved no stock. A warehouse with no snapshot today does this, and says nothing else.
    /// </summary>
    private List<TransferListenerDocumentModel> UnappliedToday =>
        status?.RecentDocuments
            .Where(document => document.LocalStock == "NotApplied"
                               && (DateTime.UtcNow - EnsureUtc(document.DetectedAt)).TotalMinutes >= UnappliedGraceMinutes
                               && Ledger is { Available: true } ledger
                               && EnsureUtc(document.DetectedAt) >= LedgerDayStartUtc(ledger))
            .ToList()
        ?? [];

    private int AppliedRecent => status?.RecentDocuments.Count(document => document.LocalStock == "Applied") ?? 0;

    // ── Verdict ─────────────────────────────────────────────────────────────

    private string VerdictTone => status switch
    {
        null => "bad",
        { Enabled: false } => "warn",
        { Reachable: false } => "bad",
        _ when IsPollBroken || IsDeliveryStuck => "bad",
        _ when IsDeliveryLate || IsPollStale || UnappliedToday.Count > 0 || status.UnwatchedWarehouses.Count > 0 => "warn",
        _ => "good"
    };

    private string VerdictHeadline => status switch
    {
        null => "The API did not answer",
        { Enabled: false } => "This API is not calling the listener",
        { Reachable: false } => "The listener is unreachable",
        _ when Poll is { PollingStarted: false } => "The listener is not reading SAP",
        _ when IsPollBroken => "The listener has stopped reading SAP",
        _ when IsDeliveryStuck => "Transfers are not reaching local stock",
        _ when IsDeliveryLate => "Transfers are reaching local stock late",
        _ when IsPollStale => "The listener's last read of SAP is late",
        _ when UnappliedToday.Count > 0 => "Some transfers moved no stock",
        _ when status.UnwatchedWarehouses.Count > 0 => "Working, with a gap in coverage",
        _ => "Transfers are reaching local stock"
    };

    private string VerdictDetail
    {
        get
        {
            if (status is null)
            {
                return (loadError ?? "The status could not be read.")
                    + " This says nothing about the listener itself — the call that failed was to the API.";
            }

            if (!status.Enabled)
            {
                return status.UnreachableReason
                    ?? "Calls out to the listener are switched off. Its inbound webhook is unaffected and still "
                       + "applies transfers to local stock.";
            }

            if (!status.Reachable)
            {
                return $"{status.UnreachableReason} No stock transfer is reaching local stock while this is true, "
                    + "and nothing else in either system reports it. " + LastAppliedSentence;
            }

            if (Poll is not { } poll)
            {
                return status.Message ?? "The listener answered without a poll status.";
            }

            if (!poll.PollingStarted)
            {
                return "The listener answers, but its SAP poll loop has not started, so no transfer is being "
                    + "read at all. " + (poll.LastError ?? string.Empty);
            }

            if (IsPollBroken)
            {
                return $"Nothing has been read from SAP for {FormatAge(poll.MinutesSinceSuccessfulPoll)}. Transfers "
                    + "made since then are missing from local stock, so a till will refuse stock the warehouse "
                    + "holds. " + (poll.LastError ?? string.Empty);
            }

            if (IsDeliveryLate && Delivery is { } delivery)
            {
                var scope = ReportsDelivery && delivery.LastDeliveredUtc is null && status.LinesSeen > 0
                    ? $"none of the {status.LinesSeen} line(s) it has found since it started on "
                      + $"{FormatShort(poll.ProcessStartedUtc)} has reached this API"
                    : $"{delivery.PendingLines} line(s) have not reached this API, the oldest for "
                      + FormatAge(OldestWaitMinutes);

                return $"The listener is reading SAP on time, but {scope}. "
                    + (IsDeliveryStuck
                        ? "Every transfer since then is missing from the tills and from Local stock."
                        : "They are retried every cycle.");
            }

            if (IsPollStale)
            {
                return $"The last successful read was {FormatAge(poll.MinutesSinceSuccessfulPoll)} ago, against a "
                    + $"{poll.PollIntervalSeconds}s cycle. The window is re-read next cycle, so nothing is lost yet.";
            }

            if (UnappliedToday is { Count: > 0 } unapplied)
            {
                return $"{unapplied.Count} document(s) from today reached this API and changed no stock: "
                    + string.Join(", ", unapplied.Select(document => document.SapDocNum?.ToString() ?? "?"))
                    + ". That is what a transfer into a warehouse with no snapshot today, or one this API does not "
                    + "monitor, looks like.";
            }

            if (status.UnwatchedWarehouses.Count > 0)
            {
                return $"{status.UnwatchedWarehouses.Count} warehouse(s) in the daily snapshot are not on the "
                    + "listener's watch list, so their stock drifts from SAP over the day.";
            }

            return $"SAP was read {FormatAge(poll.MinutesSinceSuccessfulPoll)} ago and nothing is waiting to be "
                + "delivered. " + LastAppliedSentence;
        }
    }

    private string LastAppliedSentence => Ledger switch
    {
        { Available: false } => "The API could not read its own ledger.",
        { LastAppliedUtc: { } at } ledger =>
            $"The last transfer applied to local stock was doc {ledger.LastAppliedDocNum?.ToString() ?? "?"} into "
            + $"{ledger.LastAppliedWarehouse}, at {FormatShort(at)}.",
        _ => "No transfer has ever been applied to local stock."
    };

    /// <summary>
    /// What the delivery error most likely means, because the three common answers send someone to
    /// three different places and the raw status code does not say which.
    /// </summary>
    private static string? DeliveryErrorHint(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return null;
        }

        var text = error.TrimStart();

        return text switch
        {
            _ when text.StartsWith("HTTP 404", StringComparison.OrdinalIgnoreCase) =>
                "A 404 is a wrong address, not an API that is down. Check DesktopIntegration:WebhookUrl on the listener.",
            _ when text.StartsWith("HTTP 401", StringComparison.OrdinalIgnoreCase)
                   || text.StartsWith("HTTP 403", StringComparison.OrdinalIgnoreCase) =>
                "The API refused the listener's key. Check DesktopIntegration:ApiKey on the listener.",
            _ when text.StartsWith("HTTP 5", StringComparison.OrdinalIgnoreCase) =>
                "The API failed while applying a line. Its own log names the error.",
            _ when text.StartsWith("HTTP", StringComparison.OrdinalIgnoreCase) => null,
            _ => "The listener could not open a connection to the API."
        };
    }

    /// <summary>
    /// The status part of a delivery error — "HTTP 404" out of "HTTP 404: 404 - File or directory not
    /// found." — or "No connection" for an error the API never answered.
    /// </summary>
    private static string ErrorCode(string? error)
    {
        var match = System.Text.RegularExpressions.Regex.Match(error ?? string.Empty, @"^\s*HTTP \d{3}");
        return match.Success ? match.Value.Trim() : "No connection";
    }

    // ── Pipeline ────────────────────────────────────────────────────────────

    private sealed record Chip(string Label, string Tone = "idle");

    private sealed record Stage(
        int Step,
        string Title,
        string Tone,
        string Status,
        string Figure,
        string Unit,
        string Note,
        IReadOnlyList<Chip> Chips,
        string Foot,
        bool LinksToLocalStock = false);

    private IReadOnlyList<Stage> Stages => [SapStage, DeliveryStage, LedgerStage];

    private Stage SapStage
    {
        get
        {
            if (status is null || Poll is not { } poll)
            {
                return new Stage(1, "Read from SAP", "idle", "Unknown", "—", "no poll status", UnknownNote, [],
                    "The listener could not be asked");
            }

            var tone = IsPollBroken ? "bad" : IsPollStale || poll.ConsecutiveFailures > 0 ? "warn" : "good";

            return new Stage(
                1,
                "Read from SAP",
                tone,
                !poll.PollingStarted ? "Not started" : IsPollBroken ? "Stopped" : IsPollStale ? "Late" : "On time",
                poll.LastSuccessUtc.HasValue ? FormatAgeShort(poll.MinutesSinceSuccessfulPoll) : "Never",
                poll.LastSuccessUtc.HasValue ? "since the last read" : "since it started",
                $"Polls every {poll.PollIntervalSeconds} s. A failed cycle keeps its window, so nothing is skipped.",
                [
                    new Chip($"{poll.ConsecutiveFailures} failure(s) in a row", poll.ConsecutiveFailures > 0 ? "warn" : "idle"),
                    new Chip($"{status.DocumentsSeen} docs"),
                    new Chip($"{status.LinesSeen} lines")
                ],
                string.IsNullOrWhiteSpace(poll.LastError)
                    ? "No read has failed since it started"
                    : $"Last read error {FormatShort(poll.LastErrorUtc)} — {poll.LastError}");
        }
    }

    private Stage DeliveryStage
    {
        get
        {
            if (status is null || Delivery is not { } delivery)
            {
                return new Stage(2, "Delivered to this API", "idle", "Unknown", "—", "no delivery status",
                    UnknownNote, [], "The listener could not be asked");
            }

            var tone = IsDeliveryStuck ? "bad"
                : IsDeliveryLate || delivery.RejectedLines > 0 ? "warn"
                : "good";

            var stateWord = IsDeliveryStuck ? "Failing"
                : IsDeliveryLate ? "Late"
                : delivery.PendingLines > 0 ? "Retrying"
                : "Delivering";

            var foot = delivery switch
            {
                { PendingLines: > 0, LastError: { Length: > 0 } error } =>
                    $"Last answer {ErrorCode(error)} at {FormatShort(delivery.LastErrorUtc)}",
                { LastDeliveredUtc: { } delivered } => $"Last delivered {FormatShort(delivered)}",
                _ when ReportsDelivery => $"Nothing delivered since {FormatShort(Poll?.ProcessStartedUtc)}",
                _ => "This listener does not report its delivery answers"
            };

            return new Stage(
                2,
                "Delivered to this API",
                tone,
                stateWord,
                ReportsDelivery ? DeliveredLines.ToString("N0") : delivery.PendingLines.ToString("N0"),
                ReportsDelivery ? $"of {status.LinesSeen:N0} lines" : "lines waiting",
                IsDeliveryLate
                    ? "Waiting lines are retried every cycle, oldest first, until their ledger day ends."
                    : "Each line is posted to the ledger as it is found; a failure is retried next cycle.",
                [
                    new Chip(
                        delivery.PendingLines > 0
                            ? $"{delivery.PendingLines} waiting · oldest {FormatAge(OldestWaitMinutes)}"
                            : "0 waiting",
                        delivery.PendingLines > 0 ? (IsDeliveryStuck ? "bad" : "warn") : "idle"),
                    new Chip($"{delivery.AbandonedLines} given up", delivery.AbandonedLines > 0 ? "bad" : "idle"),
                    new Chip($"{delivery.RejectedLines} rejected", delivery.RejectedLines > 0 ? "bad" : "idle")
                ],
                foot);
        }
    }

    private Stage LedgerStage
    {
        get
        {
            if (Ledger is not { Available: true } ledger)
            {
                return new Stage(3, "Applied to local stock", "idle", "Unreadable", "—", "",
                    "The API could not read its transfer adjustments.", [], "See the API log", true);
            }

            var recent = status?.RecentDocuments.Count ?? 0;
            var tone = IsDeliveryStuck || status is { Reachable: false } ? "bad"
                : IsDeliveryLate || UnappliedToday.Count > 0 ? "warn"
                : "good";

            return new Stage(
                3,
                "Applied to local stock",
                tone,
                tone switch { "bad" => "Behind", "warn" => "Gaps", _ => "Up to date" },
                ledger.MovementsToday.ToString("N0"),
                "stock movements today",
                $"Counted from this API's own transfer adjustments for the {ledger.SnapshotDate:dd MMM} snapshot.",
                recent == 0
                    ? [new Chip($"{ledger.DocumentsToday} docs today")]
                    : [
                        new Chip($"{AppliedRecent} of {recent} recent docs", AppliedRecent < recent ? tone : "good"),
                        new Chip($"{ledger.DocumentsToday} docs today")
                    ],
                ledger.LastAppliedUtc is { } at
                    ? $"Last applied {FormatShort(at)} — doc {ledger.LastAppliedDocNum?.ToString() ?? "?"}, {ledger.LastAppliedWarehouse}"
                    : "Nothing has ever been applied",
                LinksToLocalStock: true);
        }
    }

    private const string UnknownNote = "Shown once the listener answers.";

    // ── Documents and warehouses ────────────────────────────────────────────

    private static (string Tone, string Label, string Note) DocumentState(TransferListenerDocumentModel document) =>
        document.LocalStock switch
        {
            "Applied" => ("good", "Applied", FormatShort(document.AppliedAtUtc)),
            "Waiting" => ("warn", "Waiting", FormatAge((DateTime.UtcNow - EnsureUtc(document.DetectedAt)).TotalMinutes)),
            "NotApplied" => ("bad", "Not applied", "moved no stock"),
            _ => ("idle", "—", string.Empty)
        };

    private static string FirstLineSummary(TransferListenerDocumentModel document)
    {
        if (document.Lines.FirstOrDefault() is not { } line)
        {
            return string.Empty;
        }

        var summary = $"{line.ItemCode} {line.ItemDescription} × {line.Quantity:#,0.##}";
        var more = Math.Max(document.LineCount, document.Lines.Count) - 1;

        return more > 0 ? $"{summary} and {more} more" : summary;
    }

    private IEnumerable<(string Code, int Seen, int Applied)> WarehouseRows =>
        (status?.WatchedWarehouses ?? [])
        .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
        .Select(code => (
            code,
            status!.DocumentsByWarehouse.TryGetValue(code, out var seen) ? seen : 0,
            Ledger?.DocumentsTodayByWarehouse.FirstOrDefault(
                pair => string.Equals(pair.Key, code, StringComparison.OrdinalIgnoreCase)).Value ?? 0));

    // ── Formatting ──────────────────────────────────────────────────────────

    private static bool IsInbound(TransferListenerDocumentModel document) =>
        string.Equals(document.Direction, "IN", StringComparison.OrdinalIgnoreCase);

    /// <summary>The start of the ledger day in UTC: the 07:00 CAT fetch on the snapshot date.</summary>
    private static DateTime LedgerDayStartUtc(TransferListenerLedgerModel ledger) =>
        DateTime.SpecifyKind(ledger.SnapshotDate.Date.AddHours(7 - 2), DateTimeKind.Utc);

    /// <summary>
    /// Machine timestamps are UTC throughout; this is the one place they are shown to a person, so
    /// it is the one place they become CAT.
    /// </summary>
    private static string FormatClock(DateTime? utcDateTime)
        => utcDateTime.HasValue
            ? $"{IAuditService.ToCAT(EnsureUtc(utcDateTime.Value)):dd MMM HH:mm:ss} CAT"
            : "—";

    /// <summary>"12:07" today, "16 Sep 07:14" on any other day.</summary>
    private static string FormatShort(DateTime? utcDateTime)
    {
        if (!utcDateTime.HasValue)
        {
            return "—";
        }

        var cat = IAuditService.ToCAT(EnsureUtc(utcDateTime.Value));
        var today = IAuditService.ToCAT(DateTime.UtcNow).Date;

        return cat.Date == today ? $"{cat:HH:mm}" : $"{cat:dd MMM HH:mm}";
    }

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>Age in the shortest useful form. "2h 10m" beats "130 minutes" at a glance.</summary>
    private static string FormatAge(double minutes)
    {
        if (minutes < 1)
        {
            return "under a minute";
        }

        if (minutes < 60)
        {
            return $"{minutes:N0}m";
        }

        var hours = (int)(minutes / 60);
        var rest = (int)(minutes % 60);

        return hours < 24 ? $"{hours}h {rest:00}m" : $"{hours / 24}d {hours % 24}h";
    }

    /// <summary>For the large stage figure, where "under a minute" does not fit.</summary>
    private static string FormatAgeShort(double minutes) =>
        minutes < 1 ? $"{Math.Max(0, minutes * 60):N0}s" : FormatAge(minutes);
}
