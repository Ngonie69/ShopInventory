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
/// The verdict at the top is computed here rather than taken from the listener's own status word,
/// because the listener cannot see two of the four things that matter. It does not know which
/// warehouses this API snapshots, so it cannot report the drift between the two lists; and it cannot
/// tell the difference between "the API could not ask" and "the listener is down", which look
/// identical on screen and send someone to different machines.
///
/// There is no auto-refresh. The listener polls SAP every five minutes, so a page that re-read every
/// thirty seconds would mostly redraw the same numbers, and the one action on it is a write.
/// </remarks>
public partial class TransferListener
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IAuditService AuditService { get; set; } = default!;
    [Inject] private AuthenticationStateProvider AuthStateProvider { get; set; } = default!;

    /// <summary>
    /// Past this, a poll is late enough to say so. The listener's default cycle is five minutes, so
    /// this allows several missed ones before the page raises its voice. It deliberately matches
    /// <c>TransferEventListenerSettings.PollStalenessWarningMinutes</c> on the API side — the two
    /// would disagree visibly against /health/dependencies if either moved alone.
    /// </summary>
    private const double PollWarningMinutes = 20;

    /// <summary>
    /// The point at which a stale poll stops being late and becomes a fault: transfers made this long
    /// ago are still not on any till.
    /// </summary>
    private const double PollCriticalMinutes = 60;

    private bool isLoading = true;
    private bool isRefreshing;
    private bool isChecking;

    private string? alertMessage;
    private bool alertSuccess;

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
    /// processed and delivers webhooks for anything new.
    /// </summary>
    private async Task CheckNowAsync()
    {
        isChecking = true;
        alertMessage = null;

        try
        {
            var result = await Mediator.Send(new TriggerTransferListenerCheckCommand());

            if (result.IsError)
            {
                alertSuccess = false;
                alertMessage = result.FirstError.Description;
            }
            else
            {
                var check = result.Value;
                alertSuccess = !check.WebhookTriggered || check.WebhookSuccess;
                alertMessage = string.IsNullOrWhiteSpace(check.Message)
                    ? $"Checked {check.TotalSapTransfers} SAP transfer(s); "
                      + $"{check.MonitoredEventsDetected} monitored line(s) detected."
                    : check.Message;
            }

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

    // ── Verdict ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether the last successful poll is old enough to matter. Measured on the figure the API
    /// computed, which falls back to the listener's process start when no cycle has ever succeeded —
    /// so a listener that has never worked reads as stale rather than as fresh.
    /// </summary>
    private bool IsPollStale =>
        status?.Poll is { } poll && poll.MinutesSinceSuccessfulPoll >= PollWarningMinutes;

    private bool IsPollBroken =>
        status?.Poll is { } poll
        && (!poll.PollingStarted || poll.MinutesSinceSuccessfulPoll >= PollCriticalMinutes);

    private string VerdictClass => status switch
    {
        null => "tel-verdict-bad",
        { Enabled: false } => "tel-verdict-warn",
        { Reachable: false } => "tel-verdict-bad",
        _ when IsPollBroken => "tel-verdict-bad",
        _ when status.WebhookFailureCount > 0 => "tel-verdict-bad",
        _ when IsPollStale || status.UnwatchedWarehouses.Count > 0 => "tel-verdict-warn",
        _ => "tel-verdict-good"
    };

    private string VerdictIcon => VerdictClass switch
    {
        "tel-verdict-good" => "ph-check-circle",
        "tel-verdict-warn" => "ph-warning",
        _ => "ph-warning-circle"
    };

    private string VerdictHeadline => status switch
    {
        null => "The API did not answer",
        { Enabled: false } => "This API is not calling the listener",
        { Reachable: false } => "The listener is unreachable",
        _ when status.Poll is { PollingStarted: false } => "The listener is not polling SAP",
        _ when IsPollBroken => "The listener has stopped reading SAP",
        _ when status.WebhookFailureCount > 0 => "Transfers were detected but not delivered",
        _ when IsPollStale => "The listener's last read is late",
        _ when status.UnwatchedWarehouses.Count > 0 => "Polling normally, with a gap in coverage",
        _ => "The listener is reading SAP normally"
    };

    private string VerdictDetail
    {
        get
        {
            if (status is null)
            {
                return loadError ?? "The status could not be read.";
            }

            if (!status.Enabled)
            {
                return status.UnreachableReason
                    ?? "Calls out to the listener are switched off. Its inbound webhook is unaffected "
                       + "and still applies transfers to the daily snapshot.";
            }

            if (!status.Reachable)
            {
                return $"{status.UnreachableReason} No stock transfer is reaching the daily snapshot "
                    + "while this is true, and nothing else in either system reports it.";
            }

            if (status.Poll is not { } poll)
            {
                return status.Message ?? "The listener answered without a poll status.";
            }

            if (!poll.PollingStarted)
            {
                return "The listener answers, but its SAP poll loop has not started, so no transfer "
                    + "is being read at all. " + (poll.LastError ?? string.Empty);
            }

            if (IsPollBroken)
            {
                return $"Nothing has been read from SAP for {FormatAge(poll.MinutesSinceSuccessfulPoll)}. "
                    + "Transfers made since then are absent from the day's snapshot, so a till will "
                    + "refuse stock the warehouse holds. " + (poll.LastError ?? string.Empty);
            }

            if (status.WebhookFailureCount > 0)
            {
                return $"{status.WebhookFailureCount} document(s) were read from SAP but their webhook "
                    + "to this API failed, and nothing retries them. Their movements are missing from "
                    + "the snapshot until the morning fetch is run again.";
            }

            if (IsPollStale)
            {
                return $"The last successful read was {FormatAge(poll.MinutesSinceSuccessfulPoll)} ago, "
                    + $"against a {poll.PollIntervalSeconds}s cycle. The window is re-read next cycle, "
                    + "so nothing is lost yet.";
            }

            if (status.UnwatchedWarehouses.Count > 0)
            {
                return $"SAP was read {FormatAge(poll.MinutesSinceSuccessfulPoll)} ago. "
                    + $"{status.UnwatchedWarehouses.Count} warehouse(s) in the daily snapshot are not "
                    + "on the listener's watch list, so their stock drifts over the day.";
            }

            return $"SAP was read {FormatAge(poll.MinutesSinceSuccessfulPoll)} ago, against a "
                + $"{poll.PollIntervalSeconds}s cycle.";
        }
    }

    // ── Formatting ──────────────────────────────────────────────────────────

    private static bool IsInbound(TransferListenerDocumentModel document) =>
        string.Equals(document.Direction, "IN", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Machine timestamps are UTC throughout; this is the one place they are shown to a person, so
    /// it is the one place they become CAT.
    /// </summary>
    private static string FormatClock(DateTime? utcDateTime)
        => utcDateTime.HasValue
            ? $"{IAuditService.ToCAT(EnsureUtc(utcDateTime.Value)):dd MMM HH:mm:ss} CAT"
            : "—";

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

        return hours < 24 ? $"{hours}h {rest}m" : $"{hours / 24}d {hours % 24}h";
    }
}
