using System.Globalization;
using MediatR;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using ShopInventory.Web.Features.MarketBreakages.Commands.ConfirmMarketBreakage;
using ShopInventory.Web.Features.MarketBreakages.Commands.RejectMarketBreakage;
using ShopInventory.Web.Features.MarketBreakages.Queries.GetMarketBreakage;
using ShopInventory.Web.Features.MarketBreakages.Queries.GetMarketBreakages;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

/// <summary>
/// Broken stock van reps collected back from shops, and the one thing the office does with a report:
/// count what came off the van and confirm it into a transfer to returns, or reject it.
/// </summary>
/// <remarks>
/// The count starts at what the rep reported and is changed only where the office found otherwise, so
/// the usual case is one click; a changed count is drawn in the warning hue. Every action reloads the
/// report and the list from the API rather than patching what is on screen.
/// </remarks>
public partial class MarketBreakages : IDisposable
{
    private const int PageSize = 25;

    /// <summary>How long the typing rests before the list is asked again.</summary>
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// The figures across the top, which are also the status filter. "Needs you" holds the three open
    /// states, so a failed transfer is counted there as well as under its own figure.
    /// </summary>
    private static readonly (string Value, string Label, string Hint, string Family)[] Figures =
    [
        (MarketBreakageStatus.Open, "Needs you", "to count or finish", "is-accent"),
        (MarketBreakageStatus.TransferFailed, "Transfer failed", "retry or reject", "is-bad"),
        (MarketBreakageStatus.Transferred, "Transferred", "moved to returns", "is-good"),
        (MarketBreakageStatus.Rejected, "Rejected", "sent back to the rep", "is-neutral")
    ];

    /// <summary>A report still open after this long is drawn in the warning hue in the queue.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromDays(2);

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    private readonly CancellationTokenSource disposal = new();

    private List<MarketBreakageSummaryDto> reports = [];
    private Dictionary<string, int> statusCounts = [];
    private int totalCount;
    private int currentPage = 1;
    private bool isLoading = true;
    private string? errorMessage;

    private string statusFilter = MarketBreakageStatus.Open;
    private string? searchText;
    private int searchVersion;

    private int? detailId;
    private MarketBreakageDetailDto? detail;
    private bool isLoadingDetail;
    private string? detailError;
    private string? decisionRemarks;

    /// <summary>The office's count per line id, as typed. Seeded from the report on open.</summary>
    private readonly Dictionary<int, decimal> counts = [];

    private bool isSubmitting;
    private string? submittingAction;
    private bool showConfirmDialog;
    private bool rejectNeedsReason;

    /// <summary>
    /// Whether the open report is showing as a full-screen sheet. Only narrow screens draw it that way;
    /// on a wide screen the bench sits beside the queue and this changes nothing. Set by a pick, not by
    /// the report the page opens on its own, so a phone lands on the list.
    /// </summary>
    private bool sheetOpen;

    private int TotalPages => Math.Max(1, (int)Math.Ceiling(totalCount / (double)PageSize));

    private bool CanConfirm => detail is not null && MarketBreakageStatus.MayConfirm(detail.Status);

    private bool CanReject => detail is not null && MarketBreakageStatus.MayReject(detail.Status);

    private decimal CountedTotal => detail?.Lines.Sum(line => counts.GetValueOrDefault(line.Id)) ?? 0m;

    private int CountedLineCount => detail?.Lines.Count(line => counts.GetValueOrDefault(line.Id) > 0) ?? 0;

    private decimal ReportedTotal => detail?.Lines.Sum(line => line.ReportedQuantity) ?? 0m;

    /// <summary>Counted less reported: the office's finding for the whole report.</summary>
    private decimal TotalDiff => detail is null ? 0m : (CanConfirm ? CountedTotal : ReportTotal(detail)) - ReportedTotal;

    private string ReturnsText => string.IsNullOrWhiteSpace(detail?.ReturnsWarehouseCode) ? "Returns" : detail.ReturnsWarehouseCode;

    private string QueueTitle => Figures.FirstOrDefault(figure => figure.Value == statusFilter).Label ?? "All reports";

    private string ConfirmNote
    {
        get
        {
            if (detail is null)
                return string.Empty;

            var firstName = detail.ReportedByName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "the rep";
            var diff = CountedTotal - ReportedTotal;
            return diff switch
            {
                < 0 => $"You counted {QuantityDisplay.Format(-diff)} fewer than {firstName} reported; only your count moves.",
                > 0 => $"You counted {QuantityDisplay.Format(diff)} more than {firstName} reported; your count is what moves.",
                _ => $"Your count matches what {firstName} reported."
            };
        }
    }

    private string RangeLabel
    {
        get
        {
            if (totalCount == 0)
                return "No reports";

            var first = ((currentPage - 1) * PageSize) + 1;
            var last = Math.Min(currentPage * PageSize, totalCount);
            return $"{first}–{last} of {totalCount:N0}";
        }
    }

    private string EmptyMessage => !string.IsNullOrWhiteSpace(searchText)
        ? "No reports match that search"
        : statusFilter switch
        {
            MarketBreakageStatus.Open => "Nothing is waiting on the office",
            MarketBreakageStatus.TransferFailed => "No transfer is stuck",
            MarketBreakageStatus.Transferred => "No reports have been transferred to returns yet",
            MarketBreakageStatus.Rejected => "No reports have been rejected",
            _ => "No van has reported any breakages yet"
        };

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
        await OpenFirstAsync();
    }

    /// <summary>
    /// Opens the top of the queue when nothing on it is open, so the bench is never an empty panel
    /// beside a list of work.
    /// </summary>
    private async Task OpenFirstAsync()
    {
        if (detailId is int open && reports.Any(report => report.Id == open))
            return;

        if (reports.Count == 0)
        {
            detailId = null;
            detail = null;
            sheetOpen = false;
            return;
        }

        await OpenAsync(reports[0].Id);
    }

    private async Task LoadAsync()
    {
        isLoading = true;
        StateHasChanged();

        try
        {
            var result = await Mediator.Send(
                new GetMarketBreakagesQuery(statusFilter, searchText, currentPage, PageSize), disposal.Token);

            if (result.IsError)
            {
                errorMessage = result.FirstError.Description;
                reports = [];
                totalCount = 0;
                return;
            }

            reports = result.Value.Items;
            totalCount = result.Value.TotalCount;
            statusCounts = result.Value.StatusCounts;
            errorMessage = null;
        }
        catch (OperationCanceledException) when (disposal.IsCancellationRequested)
        {
        }
        finally
        {
            isLoading = false;
        }
    }

    /// <summary>
    /// The count each tab shows. "Waiting" adds the three open states, because a failed or stranded
    /// transfer is as much the office's to finish as a new report.
    /// </summary>
    private int? CountFor(string filter)
    {
        if (statusCounts.Count == 0)
            return null;

        return filter switch
        {
            MarketBreakageStatus.Open => statusCounts.GetValueOrDefault(MarketBreakageStatus.Pending)
                + statusCounts.GetValueOrDefault(MarketBreakageStatus.TransferFailed)
                + statusCounts.GetValueOrDefault(MarketBreakageStatus.Transferring),
            "" => statusCounts.Values.Sum(),
            _ => statusCounts.GetValueOrDefault(filter)
        };
    }

    private async Task RefreshAsync()
    {
        await LoadAsync();
        if (detailId is int id)
            await LoadDetailAsync(id, keepCounts: true);
    }

    private async Task SetStatusFilterAsync(string value)
    {
        if (statusFilter == value)
            return;

        statusFilter = value;
        currentPage = 1;
        await LoadAsync();
        await OpenFirstAsync();
    }

    private async Task OnSearchInputAsync(ChangeEventArgs args)
    {
        searchText = args.Value?.ToString();
        var version = ++searchVersion;

        try
        {
            await Task.Delay(SearchDebounce, disposal.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // A later keystroke has taken over; only the last one asks the API.
        if (version != searchVersion)
            return;

        currentPage = 1;
        await LoadAsync();
    }

    private async Task ClearSearchAsync()
    {
        searchText = null;
        searchVersion++;
        currentPage = 1;
        await LoadAsync();
    }

    private async Task PreviousPageAsync()
    {
        if (currentPage <= 1)
            return;

        currentPage--;
        await LoadAsync();
    }

    private async Task NextPageAsync()
    {
        if (currentPage >= TotalPages)
            return;

        currentPage++;
        await LoadAsync();
    }

    private async Task PickAsync(int id)
    {
        if (isSubmitting)
            return;

        sheetOpen = true;
        if (detailId == id && detail is not null)
            return;

        await OpenAsync(id);
    }

    private void CloseSheet() => sheetOpen = false;

    private async Task OpenAsync(int id)
    {
        detailId = id;
        showConfirmDialog = false;
        rejectNeedsReason = false;
        detail = null;
        detailError = null;
        decisionRemarks = null;
        counts.Clear();
        await LoadDetailAsync(id, keepCounts: false);
    }

    private async Task LoadDetailAsync(int id, bool keepCounts)
    {
        isLoadingDetail = true;
        StateHasChanged();

        try
        {
            var result = await Mediator.Send(new GetMarketBreakageQuery(id), disposal.Token);
            if (detailId != id)
                return; // Another report was opened while this one loaded.

            if (result.IsError)
            {
                detailError = result.FirstError.Description;
                return;
            }

            detail = result.Value;
            SeedCounts(keepCounts);
        }
        catch (OperationCanceledException) when (disposal.IsCancellationRequested)
        {
        }
        finally
        {
            if (detailId == id)
                isLoadingDetail = false;
        }
    }

    /// <summary>
    /// What the office counted if it already has, else what the rep reported. A count typed before a
    /// refresh is kept, so reloading to see a failure does not throw the office's work away.
    /// </summary>
    private void SeedCounts(bool keepCounts)
    {
        if (detail is null)
            return;

        var ids = detail.Lines.Select(line => line.Id).ToHashSet();
        foreach (var stale in counts.Keys.Where(key => !ids.Contains(key)).ToList())
            counts.Remove(stale);

        foreach (var line in detail.Lines)
        {
            if (keepCounts && counts.ContainsKey(line.Id))
                continue;
            counts[line.Id] = line.ConfirmedQuantity ?? line.ReportedQuantity;
        }
    }

    private string CountText(int lineId)
        => counts.TryGetValue(lineId, out var value) ? value.ToString("0.######", CultureInfo.InvariantCulture) : string.Empty;

    private void SetCount(int lineId, object? value)
    {
        var text = value?.ToString()?.Trim();
        counts[lineId] = decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : 0m;
    }

    /// <summary>The stepper beside the count box: one unit at a time, never below zero.</summary>
    private void StepCount(int lineId, decimal delta)
        => counts[lineId] = Math.Max(0m, counts.GetValueOrDefault(lineId) + delta);

    /// <summary>What the Counted column shows: the office's count while it can still change, else the stored one.</summary>
    private decimal CountedFor(MarketBreakageLineDto line)
        => CanConfirm ? counts.GetValueOrDefault(line.Id) : line.ConfirmedQuantity ?? line.ReportedQuantity;

    private void ClearRejectHint() => rejectNeedsReason = false;

    private void OpenConfirmDialog()
    {
        if (CountedTotal <= 0)
        {
            detailError = "Every count is zero, so there is nothing to transfer. Reject the report instead.";
            return;
        }

        detailError = null;
        showConfirmDialog = true;
    }

    private void CloseConfirmDialog()
    {
        if (!isSubmitting)
            showConfirmDialog = false;
    }

    private async Task ConfirmAsync()
    {
        if (detail is null || isSubmitting)
            return;

        var id = detail.Id;
        isSubmitting = true;
        submittingAction = "confirm";
        detailError = null;

        try
        {
            // Not the page's token: once sent, the transfer is SAP's to finish, and closing the tab
            // must not be what decides whether the office learns the outcome.
            var result = await Mediator.Send(new ConfirmMarketBreakageCommand(
                id,
                detail.Lines.Select(line => new ConfirmMarketBreakageLineDto
                {
                    LineId = line.Id,
                    ConfirmedQuantity = counts.GetValueOrDefault(line.Id)
                }).ToList(),
                decisionRemarks));

            showConfirmDialog = false;

            if (result.IsError)
            {
                detailError = result.FirstError.Description;
            }
            else
            {
                Snackbar.Add(result.Value.Message, Severity.Success);
                decisionRemarks = null;
            }
        }
        finally
        {
            isSubmitting = false;
            submittingAction = null;
        }

        // The report changed either way — transferred, or failed with the reason on it.
        await LoadAsync();
        if (detailId == id)
            await LoadDetailAsync(id, keepCounts: true);
    }

    private async Task RejectAsync()
    {
        if (detail is null || isSubmitting)
            return;

        if (string.IsNullOrWhiteSpace(decisionRemarks))
        {
            rejectNeedsReason = true;
            return;
        }

        var id = detail.Id;
        isSubmitting = true;
        submittingAction = "reject";
        detailError = null;

        try
        {
            var result = await Mediator.Send(new RejectMarketBreakageCommand(id, decisionRemarks));
            if (result.IsError)
            {
                detailError = result.FirstError.Description;
            }
            else
            {
                Snackbar.Add(result.Value.Message, Severity.Info);
                decisionRemarks = null;
            }
        }
        finally
        {
            isSubmitting = false;
            submittingAction = null;
        }

        await LoadAsync();
        if (detailId == id)
            await LoadDetailAsync(id, keepCounts: false);
    }

    private static string StatusModifier(string status) => status switch
    {
        MarketBreakageStatus.Pending => "is-accent",
        MarketBreakageStatus.Transferring => "is-info",
        MarketBreakageStatus.Transferred => "is-good",
        MarketBreakageStatus.TransferFailed => "is-bad",
        _ => "is-neutral"
    };

    /// <summary>The queue's word for a status: a pending report is one the office has to count.</summary>
    private static string StatusLabel(string status)
        => status == MarketBreakageStatus.Pending ? "To count" : MarketBreakageStatus.Describe(status);

    private static bool IsStale(MarketBreakageSummaryDto report)
        => report.Status is MarketBreakageStatus.Pending or MarketBreakageStatus.TransferFailed
           && DateTime.UtcNow - report.CapturedAtUtc >= StaleAfter;

    private static string RowAgeText(MarketBreakageSummaryDto report)
        => MarketBreakageStatus.MayConfirm(report.Status)
            ? $"Collected {AgeText(report.CapturedAtUtc)}"
            : $"Collected {FormatDate(report.CapturedAtUtc)}";

    private static string DiffText(decimal diff) => diff switch
    {
        0 => "—",
        > 0 => $"+{QuantityDisplay.Format(diff)}",
        _ => $"−{QuantityDisplay.Format(-diff)}"
    };

    private static string DiffModifier(decimal diff) => diff switch
    {
        0 => "is-even",
        > 0 => "is-over",
        _ => "is-short"
    };

    private static string UnitsText(decimal units) => units == 1 ? "1 unit" : $"{QuantityDisplay.Format(units)} units";

    private static string ProductsText(int count) => count == 1 ? "1 product" : $"{count} products";

    private static decimal ReportTotal(MarketBreakageDetailDto report)
        => report.Lines.Sum(line => line.ConfirmedQuantity ?? line.ReportedQuantity);

    private static string FormatDate(DateTime utc) => ToCat(utc).ToString("ddd dd MMM", CultureInfo.InvariantCulture);

    private static string FormatStamp(DateTime utc) => ToCat(utc).ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture);

    private static DateTime ToCat(DateTime utc) => IAuditService.ToCAT(
        utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc));

    private static string AgeText(DateTime capturedUtc)
    {
        var age = DateTime.UtcNow - capturedUtc;
        if (age < TimeSpan.FromHours(1))
            return "just now";
        if (age < TimeSpan.FromDays(1))
            return $"{(int)age.TotalHours}h ago";
        return age.TotalDays < 2 ? "yesterday" : $"{(int)age.TotalDays} days ago";
    }

    public void Dispose()
    {
        disposal.Cancel();
        disposal.Dispose();
    }
}
