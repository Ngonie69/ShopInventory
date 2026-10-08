using MediatR;
using Microsoft.AspNetCore.Components;
using ShopInventory.Web.Components.CustomerDocuments;
using ShopInventory.Web.Features.CustomerDocuments.Commands.CancelCustomerDocumentDelivery;
using ShopInventory.Web.Features.CustomerDocuments.Commands.RetryCustomerDocumentDelivery;
using ShopInventory.Web.Features.CustomerDocuments.Commands.UpdateCustomerDocumentDeliverySettings;
using ShopInventory.Web.Features.CustomerDocuments.Queries.GetCustomerDocumentDeliveryLog;
using ShopInventory.Web.Features.CustomerDocuments.Queries.GetCustomerDocumentDeliveryStatus;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Components.Pages;

/// <summary>
/// The administrators' view of documents sent to customers on WhatsApp: which number they go from,
/// today's sends against the caps, every send that needs a person, and the switches.
/// </summary>
/// <remarks>
/// <para>
/// The settings card edits the three run-time switches the sender reads on every pass, from every
/// node: the session documents go from, whether new invoices go out without anyone pressing Send,
/// and the daily cap on those. Clearing the session is the switch that stops all sending at once,
/// so it is offered as a row of the picker rather than hidden behind a confirmation.
/// </para>
/// <para>
/// A send that may already have reached the customer — sent without a confirmation, or cut off
/// mid-call — is only resent with a tick saying the customer did not receive it, the same rule the
/// invoice drawer applies; the API refuses it otherwise.
/// </para>
/// </remarks>
public partial class WhatsAppDeliveries : ComponentBase, IDisposable
{
    private const int PageSize = 50;
    private const string AttentionFilter = "attention";

    private static readonly NocturneSelectOption<string>[] StatusOptions =
    [
        NocturneSelectOption.All("Every status"),
        new(AttentionFilter, "Needs a person", "bad") { RuleAfter = true },
        StatusOption("Pending"),
        StatusOption("WaitingForFiscal"),
        StatusOption("Sending"),
        StatusOption("Uncertain"),
        StatusOption("Sent"),
        StatusOption("SentUnconfirmed"),
        StatusOption("Held"),
        StatusOption("NotOnWhatsApp"),
        StatusOption("Failed"),
        StatusOption("Cancelled"),
        StatusOption("Skipped")
    ];

    private static readonly NocturneSelectOption<string>[] TriggerOptions =
    [
        NocturneSelectOption.All("Every way"),
        TriggerOption("Auto"),
        TriggerOption("Manual"),
        TriggerOption("Counter")
    ];

    private readonly CancellationTokenSource disposal = new();

    private CustomerDocumentDeliveryStatusModel? status;
    private string? statusError;
    private bool isLoadingStatus = true;
    private DateTime? loadedAtUtc;

    private CustomerDocumentDeliveryPageModel? log;
    private string? logError;
    private bool isLoadingLog = true;

    private string? statusFilter;
    private string? triggerFilter;
    private string? searchText;
    private DateTime? fromDate;
    private DateTime? toDate;
    private int pageNumber = 1;

    private IReadOnlyList<NocturneSelectOption<string>> sessionOptions = [];
    private string? settingsSessionId;
    private bool settingsAutoSend;
    private int settingsMaxAutoPerDay;
    private bool isSavingSettings;
    private string? settingsMessage;
    private bool settingsMessageIsError;

    private long? busyDeliveryId;
    private string? notice;
    private bool noticeIsError;

    private CustomerDocumentDeliveryModel? retrying;
    private bool retryConfirmed;
    private bool isRetrying;
    private string? retryError;

    [Inject] private IMediator Mediator { get; set; } = default!;

    private bool IsRefreshing => isLoadingStatus || isLoadingLog;

    private int TotalPages => log is null || log.TotalCount == 0 ? 1 : (int)Math.Ceiling(log.TotalCount / (double)PageSize);

    private int NeedsAPerson => status is null ? 0 : status.Held + status.Uncertain + status.FailedToday;

    private bool SessionIsReady => IsReady(status?.SessionStatus);

    private bool SettingsChanged => status is not null
        && (!string.Equals(settingsSessionId ?? string.Empty, status.SessionId ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            || settingsAutoSend != status.AutoSendEnabled
            || settingsMaxAutoPerDay != status.MaxAutoPerDay);

    protected override async Task OnInitializedAsync()
    {
        await Task.WhenAll(LoadStatusAsync(), LoadLogAsync());
    }

    private async Task RefreshAsync()
    {
        notice = null;
        await Task.WhenAll(LoadStatusAsync(), LoadLogAsync());
    }

    private async Task LoadStatusAsync()
    {
        isLoadingStatus = true;
        try
        {
            var result = await Mediator.Send(new GetCustomerDocumentDeliveryStatusQuery(), disposal.Token);
            if (result.IsError)
            {
                statusError = result.FirstError.Description;
                return;
            }

            statusError = null;
            ApplyStatus(result.Value);
        }
        catch (OperationCanceledException) when (disposal.IsCancellationRequested)
        {
        }
        finally
        {
            isLoadingStatus = false;
            loadedAtUtc = DateTime.UtcNow;
        }
    }

    private void ApplyStatus(CustomerDocumentDeliveryStatusModel value)
    {
        status = value;
        settingsSessionId = value.SessionId ?? string.Empty;
        settingsAutoSend = value.AutoSendEnabled;
        settingsMaxAutoPerDay = value.MaxAutoPerDay;
        sessionOptions = BuildSessionOptions(value);
    }

    private async Task LoadLogAsync()
    {
        isLoadingLog = true;
        try
        {
            var result = await Mediator.Send(new GetCustomerDocumentDeliveryLogQuery(
                NullIfBlank(statusFilter),
                NullIfBlank(triggerFilter),
                NullIfBlank(searchText?.Trim()),
                fromDate,
                toDate,
                pageNumber,
                PageSize), disposal.Token);

            if (result.IsError)
            {
                logError = result.FirstError.Description;
                return;
            }

            logError = null;
            log = result.Value;
        }
        catch (OperationCanceledException) when (disposal.IsCancellationRequested)
        {
        }
        finally
        {
            isLoadingLog = false;
        }
    }

    private async Task ApplyFiltersAsync()
    {
        pageNumber = 1;
        await LoadLogAsync();
    }

    private async Task ShowNeedingAPersonAsync()
    {
        statusFilter = AttentionFilter;
        triggerFilter = null;
        searchText = null;
        fromDate = null;
        toDate = null;
        await ApplyFiltersAsync();
    }

    private async Task ClearFiltersAsync()
    {
        statusFilter = null;
        triggerFilter = null;
        searchText = null;
        fromDate = null;
        toDate = null;
        await ApplyFiltersAsync();
    }

    private bool HasFilters => !string.IsNullOrEmpty(statusFilter) || !string.IsNullOrEmpty(triggerFilter)
        || !string.IsNullOrWhiteSpace(searchText) || fromDate.HasValue || toDate.HasValue;

    private async Task GoToPageAsync(int target)
    {
        pageNumber = Math.Clamp(target, 1, TotalPages);
        await LoadLogAsync();
    }

    private async Task SaveSettingsAsync()
    {
        isSavingSettings = true;
        settingsMessage = null;

        try
        {
            var result = await Mediator.Send(new UpdateCustomerDocumentDeliverySettingsCommand(new UpdateCustomerDocumentDeliverySettingsModel
            {
                WhatsAppSessionId = NullIfBlank(settingsSessionId),
                AutoSendEnabled = settingsAutoSend,
                MaxAutoPerDay = settingsMaxAutoPerDay
            }), disposal.Token);

            if (result.IsError)
            {
                settingsMessage = result.FirstError.Description;
                settingsMessageIsError = true;
                return;
            }

            ApplyStatus(result.Value);
            settingsMessage = string.IsNullOrEmpty(result.Value.SessionId)
                ? "Saved. No session is chosen, so nothing will be sent."
                : "Saved. Every server applies it on its next pass.";
            settingsMessageIsError = false;
        }
        catch (OperationCanceledException) when (disposal.IsCancellationRequested)
        {
        }
        finally
        {
            isSavingSettings = false;
        }
    }

    private void ResetSettings()
    {
        if (status is not null)
        {
            ApplyStatus(status);
        }

        settingsMessage = null;
    }

    private async Task CancelAsync(CustomerDocumentDeliveryModel delivery)
    {
        busyDeliveryId = delivery.Id;
        notice = null;

        try
        {
            var result = await Mediator.Send(new CancelCustomerDocumentDeliveryCommand(delivery.Id), disposal.Token);
            if (result.IsError)
            {
                ShowNotice(result.FirstError.Description, isError: true);
                return;
            }

            ShowNotice($"{delivery.DocumentNumber} to {delivery.RecipientMasked} was withdrawn.", isError: false);
            await Task.WhenAll(LoadStatusAsync(), LoadLogAsync());
        }
        catch (OperationCanceledException) when (disposal.IsCancellationRequested)
        {
        }
        finally
        {
            busyDeliveryId = null;
        }
    }

    private void BeginRetry(CustomerDocumentDeliveryModel delivery)
    {
        retrying = delivery;
        retryConfirmed = false;
        retryError = null;
    }

    private void CloseRetry()
    {
        if (!isRetrying)
        {
            retrying = null;
        }
    }

    private async Task RetryAsync()
    {
        if (retrying is not { } delivery)
        {
            return;
        }

        isRetrying = true;
        retryError = null;

        try
        {
            var result = await Mediator.Send(
                new RetryCustomerDocumentDeliveryCommand(delivery.Id, delivery.RetryNeedsConfirmation && retryConfirmed),
                disposal.Token);

            if (result.IsError)
            {
                retryError = result.FirstError.Description;
                return;
            }

            retrying = null;
            ShowNotice($"{delivery.DocumentNumber} is queued again for {delivery.RecipientMasked}.", isError: false);
            await Task.WhenAll(LoadStatusAsync(), LoadLogAsync());
        }
        catch (OperationCanceledException) when (disposal.IsCancellationRequested)
        {
        }
        finally
        {
            isRetrying = false;
        }
    }

    private void ShowNotice(string text, bool isError)
    {
        notice = text;
        noticeIsError = isError;
    }

    /// <summary>The verdict strip: one sentence on whether documents are leaving, and what stops them.</summary>
    private (string Tone, string Icon, string Headline, string Detail) Verdict()
    {
        if (status is null)
        {
            return ("bad", "ph-warning-circle", "The sending status could not be read", statusError ?? string.Empty);
        }

        if (!status.Enabled)
        {
            return ("neutral", "ph-power", "Sending documents is switched off on the server",
                "CustomerDocuments:Enabled is off in appsettings.json, so the sender is not scheduled. Nothing queued is sent.");
        }

        if (!status.GatewayConfigured)
        {
            return ("warn", "ph-plugs", "WhatsApp is not configured on the server that answered",
                status.SessionError ?? "Its web.config has no OpenWA settings. Sends wait for a server that has them.");
        }

        if (string.IsNullOrEmpty(status.SessionId))
        {
            return ("warn", "ph-pause-circle", "No WhatsApp number is chosen to send from",
                "Nothing is sent until a session is chosen below. Documents queued meanwhile wait.");
        }

        if (!string.IsNullOrEmpty(status.SessionError))
        {
            return ("bad", "ph-warning-circle", status.SessionError,
                "Documents stay queued and go once the session is back. Check it on the WhatsApp Inbox.");
        }

        if (!SessionIsReady)
        {
            return ("bad", "ph-warning-circle", $"{status.SessionName ?? "The session"} is {StatusWord(status.SessionStatus)}",
                "Documents stay queued until it reconnects. Pair it again from the WhatsApp Inbox if it does not.");
        }

        var automatic = status.AutoSendEnabled
            ? $"New invoices go out automatically {status.AutoWindow}."
            : "Automatic sends are off; documents go when someone presses Send.";

        return ("good", "ph-check-circle",
            $"Sending from {status.SessionName}{(string.IsNullOrWhiteSpace(status.SessionPhone) ? string.Empty : $" ({status.SessionPhone})")}",
            automatic);
    }

    private static IReadOnlyList<NocturneSelectOption<string>> BuildSessionOptions(CustomerDocumentDeliveryStatusModel value)
    {
        var options = new List<NocturneSelectOption<string>>
        {
            new(string.Empty, "None — nothing is sent", "neutral") { RuleAfter = true, IsUnset = true }
        };

        options.AddRange(value.Sessions.Select(session => new NocturneSelectOption<string>(
            session.Id,
            session.Name,
            IsReady(session.Status) ? "good" : "warn")
        {
            Hint = string.IsNullOrWhiteSpace(session.Phone) ? StatusWord(session.Status) : session.Phone
        }));

        // A session saved earlier that the gateway no longer lists stays visible, so the picker never
        // shows "None" while documents are in fact waiting on a session that is gone.
        if (!string.IsNullOrEmpty(value.SessionId)
            && !value.Sessions.Any(session => string.Equals(session.Id, value.SessionId, StringComparison.OrdinalIgnoreCase)))
        {
            options.Add(new NocturneSelectOption<string>(value.SessionId, "The saved session (not on the gateway)", "bad")
            {
                Hint = value.SessionId.Length > 8 ? value.SessionId[..8] : value.SessionId
            });
        }

        return options;
    }

    private static NocturneSelectOption<string> StatusOption(string value) =>
        new(value, CustomerDocumentDisplay.StatusLabel(value), CustomerDocumentDisplay.StatusTone(value));

    private static NocturneSelectOption<string> TriggerOption(string value) =>
        new(value, CustomerDocumentDisplay.TriggerLabel(value));

    private static bool IsReady(string? sessionStatus) =>
        string.Equals(sessionStatus, "ready", StringComparison.OrdinalIgnoreCase);

    private static string StatusWord(string? sessionStatus) => sessionStatus?.ToLowerInvariant() switch
    {
        null or "" => "not reporting",
        "ready" => "ready",
        "qr_ready" => "waiting for its QR code to be scanned",
        "initializing" or "authenticating" => "connecting",
        "disconnected" => "disconnected",
        "failed" => "failed",
        var other => other.Replace('_', ' ')
    };

    private static string Of(int value, int limit) => limit > 0 ? $"{value:N0} of {limit:N0}" : $"{value:N0}";

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    public void Dispose()
    {
        disposal.Cancel();
        disposal.Dispose();
    }
}
