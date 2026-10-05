using System.Globalization;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor;
using ShopInventory.Web.Features.MarketBreakages.Commands.ConfirmMarketBreakage;
using ShopInventory.Web.Features.MarketBreakages.Commands.RejectMarketBreakage;
using ShopInventory.Web.Features.MarketBreakages.Queries.ExportMarketBreakages;
using ShopInventory.Web.Features.MarketBreakages.Queries.GetMarketBreakage;
using ShopInventory.Web.Features.MarketBreakages.Queries.GetMarketBreakages;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

/// <summary>
/// Stock broken in transit that van reps report, and the one thing the office does with a report:
/// count what came off the van and confirm it into a transfer to returns, or reject it.
/// </summary>
/// <remarks>
/// Every line starts uncounted and has to be counted — typed, stepped, or filled from what the rep
/// reported — before the report can move, so a transfer is never the rep's figures by default. A
/// count that differs from the report needs a remark, because the rep sees it. Every action reloads
/// the report and the list from the API rather than patching what is on screen, and a decided report
/// hands the drawer to the next one in the queue.
/// </remarks>
public partial class MarketBreakages : IDisposable
{
    private const int PageSize = 25;

    /// <summary>How long the typing rests before the list is asked again.</summary>
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// The tabs across the top. "Needs you" holds the three open states — to count, failed and
    /// stranded — because each is the office's to finish; the API orders it failed first, then oldest.
    /// </summary>
    private static readonly (string Value, string Label)[] Tabs =
    [
        (MarketBreakageStatus.Open, "Needs you"),
        (MarketBreakageStatus.Transferred, "Transferred"),
        (MarketBreakageStatus.Rejected, "Rejected"),
        (string.Empty, "All")
    ];

    /// <summary>An open report this many days old is drawn in the warning hue in the queue.</summary>
    private const int OverdueDays = 5;

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;

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

    /// <summary>Whether the drawer is showing <see cref="detailId"/>.</summary>
    private bool drawerOpen;
    private ElementReference drawerElement;
    private bool focusDrawer;

    /// <summary>
    /// The office's count per line id. A line with no entry has not been counted yet; it is seeded only
    /// from a count the office already confirmed, as on a failed transfer.
    /// </summary>
    private readonly Dictionary<int, decimal> counts = [];

    private bool isSubmitting;
    private string? submittingAction;
    private bool rejectNeedsReason;

    /// <summary>The file being built, if any. One at a time: both buttons wait on it.</summary>
    private MarketBreakageExportFormat? exporting;

    private bool CanExport => exporting is null && !isLoading && totalCount > 0;

    private int TotalPages => Math.Max(1, (int)Math.Ceiling(totalCount / (double)PageSize));

    private bool CanConfirm => detail is not null && MarketBreakageStatus.MayConfirm(detail.Status);

    private bool CanReject => detail is not null && MarketBreakageStatus.MayReject(detail.Status);

    private int CountedLines => detail?.Lines.Count(line => CountedFor(line) is not null) ?? 0;

    private bool AllCounted => detail is not null && CountedLines == detail.Lines.Count;

    private decimal CountedTotal => detail?.Lines.Sum(line => CountedFor(line) ?? 0m) ?? 0m;

    private decimal ReportedTotal => detail?.Lines.Sum(line => line.ReportedQuantity) ?? 0m;

    /// <summary>Counted less reported, for the whole report.</summary>
    private decimal TotalDiff => CountedTotal - ReportedTotal;

    /// <summary>
    /// Whether any line was counted other than reported. Per line, not the total, so a short line and
    /// an over line that cancel out still need saying.
    /// </summary>
    private bool CountDiffers => detail?.Lines.Any(line => CountedFor(line) is decimal counted && counted != line.ReportedQuantity) ?? false;

    private bool HasRemark => !string.IsNullOrWhiteSpace(decisionRemarks);

    /// <summary>A finished count that differs from the report, with nothing said about why.</summary>
    private bool RemarkMissing => CanConfirm && AllCounted && CountDiffers && !HasRemark;

    private bool CanSubmitCount => CanConfirm && AllCounted && !RemarkMissing && CountedTotal > 0;

    private int ProgressPercent => detail is null || detail.Lines.Count == 0
        ? 0
        : (int)Math.Round(CountedLines * 100d / detail.Lines.Count);

    private string ProgressLabel
    {
        get
        {
            if (detail is null)
                return string.Empty;
            if (!CanConfirm)
                return "Count closed";
            return AllCounted
                ? $"All {LinesText(detail.Lines.Count)} counted"
                : $"{CountedLines} of {detail.Lines.Count} counted";
        }
    }

    private string TotalDiffLabel => TotalDiff == 0 ? "None" : SignedText(TotalDiff);

    private string RemarkHint
    {
        get
        {
            if (rejectNeedsReason)
                return "Say why you are rejecting it — the rep needs the reason.";
            if (CanConfirm && AllCounted && CountDiffers)
            {
                var detailText = TotalDiff switch
                {
                    < 0 => $"your count is {QuantityDisplay.Format(-TotalDiff)} short",
                    > 0 => $"your count is {QuantityDisplay.Format(TotalDiff)} over",
                    _ => "your count differs from the rep's line by line"
                };
                return $"Required: {detailText}. The rep sees this.";
            }

            return CanReject
                ? "Optional to confirm, required to reject. The rep sees this."
                : "Optional. The rep sees this.";
        }
    }

    private string ConfirmLabel
    {
        get
        {
            if (detail is null)
                return string.Empty;
            if (!AllCounted)
                return $"Count {(detail.Lines.Count - CountedLines == 1 ? "1 more line" : $"{detail.Lines.Count - CountedLines} more lines")}";
            if (RemarkMissing)
                return "Add a remark to confirm";
            if (CountedTotal <= 0)
                return "Nothing to transfer — reject it";
            return detail.Status == MarketBreakageStatus.Pending
                ? $"Confirm & transfer {UnitsText(CountedTotal)}"
                : $"Retry transfer · {UnitsText(CountedTotal)}";
        }
    }

    private string MoveLabel
    {
        get
        {
            if (detail is null)
                return string.Empty;
            if (!CanConfirm)
                return $"{UnitsText(CountedLines > 0 ? CountedTotal : ReportedTotal)} · {ProductsText(detail.Lines.Count)}";
            return AllCounted
                ? $"{UnitsText(CountedTotal)} · {ProductsText(detail.Lines.Count(line => CountedFor(line) > 0))}"
                : $"{UnitsText(ReportedTotal)} · {ProductsText(detail.Lines.Count)} reported";
        }
    }

    private string DoneLabel
    {
        get
        {
            if (detail is null)
                return string.Empty;

            var who = detail.DecidedByName ?? "the office";
            if (detail.Status == MarketBreakageStatus.Transferred)
            {
                var transfer = detail.SapDocNum is int num ? $" as transfer #{num}" : "";
                var when = detail.TransferredAtUtc is DateTime at ? $", {FormatStamp(at)}" : "";
                return $"Moved to returns in SAP{transfer} by {who}{when}";
            }

            var decided = detail.DecidedAtUtc is DateTime decidedAt ? $", {FormatStamp(decidedAt)}" : "";
            return $"Sent back to the rep by {who}{decided}";
        }
    }

    private string ReturnsText => string.IsNullOrWhiteSpace(detail?.ReturnsWarehouseCode) ? "Returns" : detail.ReturnsWarehouseCode;

    private string ListTitle => Tabs.FirstOrDefault(tab => tab.Value == statusFilter).Label ?? "All reports";

    private string OrderNote => statusFilter == MarketBreakageStatus.Open
        ? "Failed transfers first, then oldest"
        : "Newest first";

    private int CurrentIndex => detailId is int id ? reports.FindIndex(report => report.Id == id) : -1;

    private int? PreviousId => CurrentIndex > 0 ? reports[CurrentIndex - 1].Id : null;

    private int? NextId => CurrentIndex >= 0 && CurrentIndex < reports.Count - 1 ? reports[CurrentIndex + 1].Id : null;

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
            MarketBreakageStatus.Transferred => "No reports have been transferred to returns yet",
            MarketBreakageStatus.Rejected => "No reports have been rejected",
            _ => "No van has reported any breakages yet"
        };

    protected override Task OnInitializedAsync() => LoadAsync();

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!focusDrawer || !drawerOpen)
            return;

        focusDrawer = false;
        try
        {
            await drawerElement.FocusAsync();
        }
        catch (JSDisconnectedException)
        {
        }
        catch (InvalidOperationException)
        {
            // The drawer closed again before this render reached the browser.
        }
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
    /// The count each tab shows. "Needs you" adds the three open states, because a failed or stranded
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
        if (drawerOpen && detailId is int id)
            await LoadDetailAsync(id, keepCounts: true);
    }

    /// <summary>
    /// Downloads the reports the list holds now — this tab, this search, every page — as a workbook
    /// or a PDF.
    /// </summary>
    private async Task ExportAsync(MarketBreakageExportFormat format)
    {
        if (!CanExport)
            return;

        exporting = format;
        try
        {
            var result = await Mediator.Send(
                new ExportMarketBreakagesQuery(statusFilter, searchText, format), disposal.Token);
            if (result.IsError)
            {
                Snackbar.Add(result.FirstError.Description, Severity.Error);
                return;
            }

            var file = result.Value;
            await JS.InvokeVoidAsync("downloadFile", disposal.Token, file.FileName, file.ContentType, Convert.ToBase64String(file.Content));
        }
        catch (OperationCanceledException) when (disposal.IsCancellationRequested)
        {
        }
        catch (JSDisconnectedException)
        {
        }
        finally
        {
            exporting = null;
        }
    }

    private async Task SetStatusFilterAsync(string value)
    {
        if (statusFilter == value)
            return;

        statusFilter = value;
        currentPage = 1;
        await LoadAsync();
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

        if (!drawerOpen)
            focusDrawer = true;
        drawerOpen = true;

        if (detailId == id && detail is not null)
            return;

        await OpenAsync(id);
    }

    private void CloseDrawer()
    {
        if (!isSubmitting)
            drawerOpen = false;
    }

    private void OnDrawerKeyDown(KeyboardEventArgs args)
    {
        if (args.Key == "Escape")
            CloseDrawer();
    }

    private async Task OpenAsync(int id)
    {
        detailId = id;
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
    /// What the office already confirmed, where it has; every other line starts uncounted. A count
    /// typed before a refresh is kept, so reloading to see a failure does not throw the work away.
    /// </summary>
    private void SeedCounts(bool keepCounts)
    {
        if (detail is null)
            return;

        if (!keepCounts)
            counts.Clear();

        var ids = detail.Lines.Select(line => line.Id).ToHashSet();
        foreach (var stale in counts.Keys.Where(key => !ids.Contains(key)).ToList())
            counts.Remove(stale);

        foreach (var line in detail.Lines)
        {
            if (!counts.ContainsKey(line.Id) && line.ConfirmedQuantity is decimal confirmed)
                counts[line.Id] = confirmed;
        }
    }

    /// <summary>
    /// The Counted column: the office's count while it can still change, else the stored one. Null is
    /// a line nobody has counted.
    /// </summary>
    private decimal? CountedFor(MarketBreakageLineDto line)
        => CanConfirm
            ? counts.TryGetValue(line.Id, out var value) ? value : null
            : line.ConfirmedQuantity;

    private string CountText(int lineId)
        => counts.TryGetValue(lineId, out var value) ? value.ToString("0.######", CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>A cleared box makes the line uncounted again; anything unreadable or negative counts as zero.</summary>
    private void SetCount(int lineId, object? value)
    {
        var text = value?.ToString()?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            counts.Remove(lineId);
            return;
        }

        counts[lineId] = decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : 0m;
    }

    /// <summary>
    /// The stepper beside the count box. On an uncounted line plus takes the reported figure and minus
    /// one below it; after that, one unit at a time and never below zero.
    /// </summary>
    private void StepCount(MarketBreakageLineDto line, decimal delta)
    {
        var start = counts.TryGetValue(line.Id, out var current) ? current : line.ReportedQuantity - (delta > 0 ? delta : 0m);
        counts[line.Id] = Math.Max(0m, start + delta);
    }

    private void FillRestWithReported()
    {
        if (detail is null)
            return;

        foreach (var line in detail.Lines)
            counts.TryAdd(line.Id, line.ReportedQuantity);
    }

    private void ClearRejectHint() => rejectNeedsReason = false;

    private async Task ConfirmAsync()
    {
        if (detail is null || isSubmitting || !CanSubmitCount)
            return;

        var id = detail.Id;
        isSubmitting = true;
        submittingAction = "confirm";
        detailError = null;
        var transferred = false;

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

            if (result.IsError)
            {
                detailError = result.FirstError.Description;
            }
            else
            {
                Snackbar.Add(result.Value.Message, Severity.Success);
                transferred = true;
            }
        }
        finally
        {
            isSubmitting = false;
            submittingAction = null;
        }

        await AfterDecisionAsync(id, decided: transferred);
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
        var rejected = false;

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
                rejected = true;
            }
        }
        finally
        {
            isSubmitting = false;
            submittingAction = null;
        }

        await AfterDecisionAsync(id, decided: rejected);
    }

    /// <summary>
    /// Reloads the list after a confirm or reject. A decided report that has left this tab hands the
    /// drawer to whichever report took its place — the next one to work in "Needs you" — and the drawer
    /// closes when the tab is empty. A failure, or a report still in the tab, stays open and reloads.
    /// </summary>
    private async Task AfterDecisionAsync(int id, bool decided)
    {
        var index = CurrentIndex;
        var attemptBefore = detail?.LastAttemptedAtUtc;
        await LoadAsync();

        if (detailId != id)
            return;

        if (!decided || reports.Any(report => report.Id == id))
        {
            await LoadDetailAsync(id, keepCounts: !decided);
            if (decided)
                decisionRemarks = null;

            // A transfer SAP refused is recorded on the report, and its banner already says why.
            if (!decided && detail?.Status == MarketBreakageStatus.TransferFailed
                && detail.LastAttemptedAtUtc != attemptBefore && !string.IsNullOrWhiteSpace(detail.LastError))
                detailError = null;
            return;
        }

        if (reports.Count == 0)
        {
            drawerOpen = false;
            detailId = null;
            detail = null;
            return;
        }

        await OpenAsync(reports[Math.Clamp(index, 0, reports.Count - 1)].Id);
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

    private static string SummaryModifier(string status) => status switch
    {
        MarketBreakageStatus.TransferFailed => "is-bad",
        MarketBreakageStatus.Transferring => "is-info",
        _ => ""
    };

    /// <summary>The line under the product: the status, then the reasons the rep gave.</summary>
    private static string RowSummary(MarketBreakageSummaryDto report)
    {
        if (report.Status == MarketBreakageStatus.TransferFailed)
            return "Transfer failed";
        if (report.Status == MarketBreakageStatus.Transferring)
            return "Transfer not finished";

        var reasons = report.Reasons.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return reasons.Count == 0 ? StatusLabel(report.Status) : $"{StatusLabel(report.Status)} · {string.Join(", ", reasons)}";
    }

    private static bool IsOverdue(MarketBreakageSummaryDto report)
        => MarketBreakageStatus.MayConfirm(report.Status) && DaysWaiting(report.CapturedAtUtc) >= OverdueDays;

    /// <summary>How long an open report has waited; a decided one waits on nobody.</summary>
    private static string WaitingText(MarketBreakageSummaryDto report)
    {
        if (!MarketBreakageStatus.MayConfirm(report.Status))
            return "—";

        var days = DaysWaiting(report.CapturedAtUtc);
        return days switch
        {
            0 => "Today",
            1 => "1 day",
            _ => $"{days} days"
        };
    }

    /// <summary>Calendar days in Harare between the report and today.</summary>
    private static int DaysWaiting(DateTime capturedUtc)
        => Math.Max(0, (ToCat(DateTime.UtcNow).Date - ToCat(capturedUtc).Date).Days);

    private static string AgeLong(DateTime capturedUtc) => DaysWaiting(capturedUtc) switch
    {
        0 => "today",
        1 => "yesterday",
        var days => $"{days} days ago"
    };

    private static string DiffLabel(decimal? counted, decimal reported)
    {
        if (counted is not decimal value)
            return "To count";

        var diff = value - reported;
        return diff switch
        {
            0 => "Matches",
            < 0 => $"{SignedText(diff)} short",
            _ => $"{SignedText(diff)} over"
        };
    }

    private static string DiffModifier(decimal? counted, decimal reported)
        => counted is decimal value ? DiffFamily(value - reported) : "is-pending";

    private static string DiffFamily(decimal diff) => diff switch
    {
        0 => "is-even",
        < 0 => "is-short",
        _ => "is-over"
    };

    private static string SignedText(decimal diff) => diff switch
    {
        > 0 => $"+{QuantityDisplay.Format(diff)}",
        < 0 => $"−{QuantityDisplay.Format(-diff)}",
        _ => "0"
    };

    private static string UnitsText(decimal units) => units == 1 ? "1 unit" : $"{QuantityDisplay.Format(units)} units";

    private static string ProductsText(int count) => count == 1 ? "1 product" : $"{count} products";

    private static string LinesText(int count) => count == 1 ? "1 line" : $"{count} lines";

    private static string FormatStamp(DateTime utc) => ToCat(utc).ToString("dd MMM yyyy, HH:mm", CultureInfo.InvariantCulture);

    private static DateTime ToCat(DateTime utc) => IAuditService.ToCAT(
        utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc));

    public void Dispose()
    {
        disposal.Cancel();
        disposal.Dispose();
    }
}
