using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using ShopInventory.Web.Components;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

public partial class VanReplenishment
{
    [Inject] private IVanSalesReportService VanSalesReportService { get; set; } = default!;
    [Inject] private IInventoryTransferService InventoryTransferService { get; set; } = default!;
    [Inject] private IReportExportService ExportService { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    private enum DrawerMode { View, Record, ConfirmWithdraw, ConfirmPostInStock, ConfirmRetry }

    private enum BulkKind { PostInStock, Withdraw }

    private sealed class DrawerState(VanReplenishmentOpenRequest request)
    {
        public VanReplenishmentOpenRequest Request { get; } = request;
        public DrawerMode Mode { get; set; } = DrawerMode.View;
        public PendingTransferStockCheck? Stock { get; set; }
        public bool StockLoading { get; set; } = true;
        public bool Busy { get; set; }
        public string? Error { get; set; }
    }

    private sealed class BulkState(BulkKind kind, string depot, List<VanReplenishmentOpenRequest> requests)
    {
        public BulkKind Kind { get; } = kind;
        public string Depot { get; } = depot;
        public List<VanReplenishmentOpenRequest> Requests { get; } = requests;
        public string Reason { get; set; } = string.Empty;
        public bool Running { get; set; }
        public int Done { get; set; }
        public List<string> Failures { get; } = [];
    }

    private VanReplenishmentReportResponse? report;

    private DateTime? fromDate;
    private DateTime? toDate;
    private string? selectedVan;
    private string? selectedDepot;

    private bool isLoading;
    private string? loadError;

    private DrawerState? drawer;
    private CancellationTokenSource? drawerCts;
    private string? docNumText;
    private string? withdrawReason;

    private BulkState? bulk;

    /// <summary>
    /// Every van a rep is assigned to, from the report's own list — taken before any filter applied,
    /// so choosing one van never shrinks the list of the others.
    /// </summary>
    private IEnumerable<INocturneSelectOption<string>> VanFilterOptions =>
        new[] { NocturneSelectOption.All("All vans") }
            .Concat((report?.AvailableVans ?? [])
                .Select(van => new NocturneSelectOption<string>(van, van)));

    private List<string> DepotChoices => report?.AvailableDepots ?? [];

    private int PeriodDays => report is null ? 0 : (int)(report.ToDate - report.FromDate).TotalDays + 1;

    private IEnumerable<VanReplenishmentWaitBand> VisibleWaits =>
        (report?.Waits ?? []).Where(band => band.Count > 0);

    private string WaitsAriaLabel => string.Join(", ",
        VisibleWaits.Select(band => $"{VanReplenishmentWaitBands.Label(band.Band)}: {band.Count}"));

    protected override async Task OnInitializedAsync()
    {
        using var pageReads = PageReads.Bind(PageLifetime);
        // CAT, not the server's clock: a trading day belongs to the van.
        var today = DateTime.UtcNow.AddHours(2).Date;
        fromDate = today.AddDays(-29);
        toDate = today;

        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        isLoading = true;
        loadError = null;
        StateHasChanged();

        try
        {
            var loaded = await VanSalesReportService.GetReplenishmentReportAsync(
                fromDate,
                toDate,
                string.IsNullOrWhiteSpace(selectedVan) ? null : selectedVan,
                selectedDepot);

            if (loaded is null)
            {
                loadError = "The replenishment report could not be loaded. Check that the API is reachable.";
                return;
            }

            report = loaded;
        }
        catch (Exception ex)
        {
            loadError = $"Failed to load the replenishment report: {ex.Message}";
        }
        finally
        {
            isLoading = false;
            StateHasChanged();
        }
    }

    private async Task PickDepotAsync(string? depot)
    {
        if (selectedDepot == depot)
        {
            return;
        }

        selectedDepot = depot;
        await LoadAsync();
    }

    private async Task ExportAsync()
    {
        if (report is null)
        {
            return;
        }

        try
        {
            var bytes = ExportService.ExportVanReplenishmentToExcel(report);
            var fileName = $"VanReplenishment_{DateTime.UtcNow.AddHours(2):yyyyMMdd_HHmmss}.xlsx";

            await JS.InvokeVoidAsync("downloadFile", fileName, Convert.ToBase64String(bytes));
        }
        catch (Exception ex)
        {
            loadError = $"The export could not be downloaded: {ex.Message}";
        }
    }

    // ── The drawer ──────────────────────────────────────────────────────────────

    private async Task OpenAsync(VanReplenishmentOpenRequest request, DrawerMode mode)
    {
        drawerCts?.Cancel();
        drawerCts?.Dispose();
        drawerCts = new CancellationTokenSource();

        drawer = new DrawerState(request) { Mode = mode };
        docNumText = null;
        withdrawReason = null;
        StateHasChanged();

        // The stock the drawer shows is read now, not the shortage recorded at the last attempt,
        // which may be weeks old. An approval still pending needs no stock read to be understood.
        if (request.Cause == VanReplenishmentCauses.AwaitingDecision || request.Cause == VanReplenishmentCauses.Posting)
        {
            drawer.StockLoading = false;
            return;
        }

        var opened = drawer;
        var token = drawerCts.Token;
        var stock = await InventoryTransferService.CheckPendingTransferStockAsync(request.Id, token);
        if (token.IsCancellationRequested || !ReferenceEquals(drawer, opened))
        {
            return;
        }

        opened.Stock = stock;
        opened.StockLoading = false;
        StateHasChanged();
    }

    private void CloseDrawer()
    {
        drawerCts?.Cancel();
        drawer = null;
    }

    private void SetMode(DrawerMode mode)
    {
        if (drawer is null)
        {
            return;
        }

        drawer.Mode = mode;
        drawer.Error = null;
    }

    /// <summary>
    /// A partial post is worth offering when the depot can fill some lines and not others, and every
    /// line was actually measured — nothing is posted against stock nobody read.
    /// </summary>
    private bool CanPostInStock =>
        drawer?.Stock is { StockWasFullyRead: true, LinesShort: > 0, LinesInStock: > 0 }
        && drawer.Request.Status is "PostFailed" or "Approved";

    private string PostInStockExplainer => drawer?.Stock switch
    {
        null => "The lines could not be measured, so only a retry of the whole transfer is offered.",
        { LinesShort: 0 } => "Every line is in stock now, so the whole transfer can post.",
        { LinesInStock: 0 } => $"None of the lines is in stock at {drawer.Request.DepotWarehouseCode}. Retrying changes nothing until it is restocked.",
        var stock => $"Posting the lines in stock sends {stock.LinesInStock} lines to {drawer.Request.VanWarehouseCode} now and closes this request. " +
                     $"The {Plural(stock.LinesShort, "short line")} are dropped, not held — the rep asks again once {drawer.Request.DepotWarehouseCode} has them."
    };

    private string RetryLabel => drawer switch
    {
        { Request.Cause: VanReplenishmentCauses.ApprovedNeverPosted } => "Post it to SAP",
        { Stock.LinesShort: 0 } => $"Post all {drawer.Request.LineCount} lines",
        _ => $"Retry all {drawer!.Request.LineCount} lines"
    };

    private async Task PostInStockAsync()
    {
        if (drawer is null) return;

        var current = drawer;
        current.Busy = true;
        current.Error = null;

        var (success, message, _) = await InventoryTransferService.PostPendingTransferLinesInStockAsync(current.Request.Id);
        await FinishActionAsync(current, success, message);
    }

    private async Task RetryAsync()
    {
        if (drawer is null) return;

        var current = drawer;
        current.Busy = true;
        current.Error = null;

        var (success, message, _) = await InventoryTransferService.RetryPendingTransferPostAsync(current.Request.Id);
        await FinishActionAsync(current, success, message);
    }

    private async Task WithdrawAsync()
    {
        if (drawer is null || string.IsNullOrWhiteSpace(withdrawReason)) return;

        var current = drawer;
        current.Busy = true;
        current.Error = null;

        var (success, message) = await InventoryTransferService.WithdrawPendingTransferAsync(current.Request.Id, withdrawReason.Trim());
        await FinishActionAsync(current, success, message);
    }

    private async Task RecordAsync()
    {
        if (drawer is null || !int.TryParse(docNumText, out var docNum)) return;

        var current = drawer;
        current.Busy = true;
        current.Error = null;

        var (success, message, _) = await InventoryTransferService.RecordPendingTransferSapDocumentAsync(current.Request.Id, docNum);
        await FinishActionAsync(current, success, message);
    }

    /// <summary>
    /// A success closes the drawer and re-reads the report, since the request has left the list; a
    /// refusal stays in the drawer, in the API's words, beside the button that caused it.
    /// </summary>
    private async Task FinishActionAsync(DrawerState current, bool success, string message)
    {
        current.Busy = false;

        if (!success)
        {
            current.Error = message;
            StateHasChanged();
            return;
        }

        Snackbar.Add(message, Severity.Success);
        if (ReferenceEquals(drawer, current))
        {
            CloseDrawer();
        }

        await LoadAsync();
    }

    // ── Acting on a whole depot's group ─────────────────────────────────────────

    private void StartBulk(BulkKind kind, List<VanReplenishmentOpenRequest> requests)
    {
        if (requests.Count == 0) return;
        bulk = new BulkState(kind, requests[0].DepotWarehouseCode, requests);
    }

    private void CancelBulk() => bulk = null;

    /// <summary>
    /// One request at a time, each through the same endpoint the drawer uses, so every one gets the
    /// same live stock read and the same guard. A refusal is listed and the rest carry on.
    /// </summary>
    private async Task RunBulkAsync()
    {
        if (bulk is null || bulk.Running) return;

        var run = bulk;
        run.Running = true;
        StateHasChanged();

        foreach (var request in run.Requests)
        {
            var (success, message) = run.Kind == BulkKind.PostInStock
                ? await PostInStockForBulkAsync(request)
                : await InventoryTransferService.WithdrawPendingTransferAsync(request.Id, run.Reason.Trim());

            run.Done++;
            if (!success)
            {
                run.Failures.Add($"{request.VanWarehouseCode} {request.DraftNumber}: {message}");
            }

            StateHasChanged();
        }

        run.Running = false;
        var succeeded = run.Done - run.Failures.Count;
        if (succeeded > 0)
        {
            Snackbar.Add(
                run.Kind == BulkKind.PostInStock
                    ? $"Posted the lines in stock for {Plural(succeeded, "request")}."
                    : $"Withdrew {Plural(succeeded, "request")}.",
                Severity.Success);
        }

        StateHasChanged();
    }

    private async Task<(bool Success, string Message)> PostInStockForBulkAsync(VanReplenishmentOpenRequest request)
    {
        var (success, message, _) = await InventoryTransferService.PostPendingTransferLinesInStockAsync(request.Id);
        return (success, message);
    }

    private async Task FinishBulkAsync()
    {
        bulk = null;
        await LoadAsync();
    }

    public void Dispose()
    {
        drawerCts?.Cancel();
        drawerCts?.Dispose();
    }

    // ── Reading the report ──────────────────────────────────────────────────────

    private List<VanReplenishmentOpenRequest> ByCause(string cause) =>
        report?.Unfilled.Where(request => request.Cause == cause).ToList() ?? [];

    /// <summary>
    /// The van that has asked most often without a load, when one stands out — the line that says a
    /// rep keeps asking for the same thing.
    /// </summary>
    private string? LongestWaitingVan(List<VanReplenishmentOpenRequest> rows)
    {
        var worst = rows
            .GroupBy(request => request.VanWarehouseCode)
            .Where(group => group.Count() >= 3)
            .OrderByDescending(group => group.Count())
            .FirstOrDefault();
        if (worst is null)
        {
            return null;
        }

        var first = worst.Min(request => request.RequestedAt);
        var van = report?.Vans.FirstOrDefault(candidate => candidate.VanWarehouseCode == worst.Key);
        var lastLoad = van?.LastPostedAt is { } posted
            ? $"its last load was {posted:d MMM}"
            : "it has never had a load";

        return $"{worst.Key} has asked {worst.Count()} times since {first:d MMM} and is still waiting; {lastLoad}.";
    }

    private static string ShortSummary(VanReplenishmentOpenRequest request)
    {
        if (request.ShortItems.Count == 0)
        {
            return "—";
        }

        var shown = string.Join(" · ", request.ShortItems.Take(3).Select(item => $"{item.ItemCode} −{Qty(item.Shortage)}"));
        return request.ShortItems.Count > 3 ? $"{shown} · +{request.ShortItems.Count - 3}" : shown;
    }

    private static string AttemptTitle(VanReplenishmentOpenRequest request) => request.Cause switch
    {
        VanReplenishmentCauses.DepotShort => "SAP refused the post: stock short",
        VanReplenishmentCauses.OutcomeUnknown => "Post sent; SAP never answered",
        VanReplenishmentCauses.StockUnread => "SAP could not read the depot's stock",
        VanReplenishmentCauses.ApprovedNeverPosted => "Post started and never finished",
        _ => "SAP refused the post"
    };

    private static string CauseChip(string cause) => cause switch
    {
        VanReplenishmentCauses.OutcomeUnknown or VanReplenishmentCauses.StockUnread => "vrp-chip-warn",
        VanReplenishmentCauses.AwaitingDecision or VanReplenishmentCauses.Posting => "vrp-chip-accent",
        _ => "vrp-chip-bad"
    };

    private static string BandClass(string band) => band switch
    {
        VanReplenishmentWaitBands.UnderOneHour => "fast",
        VanReplenishmentWaitBands.OneToFourHours => "ok",
        VanReplenishmentWaitBands.FourToTwentyFourHours => "day",
        VanReplenishmentWaitBands.OneToThreeDays => "slow",
        VanReplenishmentWaitBands.OverThreeDays => "slower",
        VanReplenishmentWaitBands.StillOpen or VanReplenishmentWaitBands.WithdrawnAfterFailure => "unfilled",
        _ => "neutral"
    };

    private static string WaitClass(VanReplenishmentOpenRequest request) =>
        request.DaysWaiting >= 7 ? "vrp-bad-ink" : request.DaysWaiting >= 1 ? "vrp-warn-ink" : string.Empty;

    private static string? FillClass(double? rate) => rate switch
    {
        null => "vrp-faint",
        < .5 => "vrp-bad-ink",
        < .8 => "vrp-warn-ink",
        _ => null
    };

    private static string? VanRowClass(VanReplenishmentVan van) =>
        van.RequestCount == 0 ? "is-idle" : van.UnfilledNowCount > 0 ? "is-bad" : null;

    private static string Outcome(VanReplenishmentVan van)
    {
        if (van.RequestCount == 0)
        {
            return "asked for nothing";
        }

        var parts = new List<string> { $"{van.PostedCount:N0} posted" };
        if (van.PartlyPostedCount > 0) parts[0] += $" ({van.PartlyPostedCount:N0} in part)";
        if (van.UnfilledCount > 0) parts.Add($"{van.UnfilledCount:N0} unfilled");
        if (van.RejectedCount > 0) parts.Add($"{van.RejectedCount:N0} turned down");
        if (van.CancelledCount > 0) parts.Add($"{van.CancelledCount:N0} taken back");
        return string.Join(" · ", parts);
    }

    private static string LastLoadNote(VanReplenishmentVan van)
    {
        if (van.LastPostedBeforePeriod)
        {
            return "before this period";
        }

        return van.DaysSinceLastPosted switch
        {
            0 => "today",
            1 => "yesterday",
            { } days => $"{days} days ago",
            _ => string.Empty
        };
    }

    /// <summary>Each unit of the outcome bar is a request, capped so a busy van cannot push the row wide.</summary>
    private string OutcomeWidth(int count)
    {
        var most = Math.Max(1, report?.Vans.Max(van => van.RequestCount) ?? 1);
        return $"{Math.Round(count * 110.0 / most, 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}px";
    }

    private static string MeterWidth(int part, int whole) =>
        whole <= 0 ? "0%" : $"{Math.Round(100.0 * part / whole).ToString(System.Globalization.CultureInfo.InvariantCulture)}%";

    private static string Rate(double? rate) => rate is { } value ? value.ToString("P0") : "—";

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count:N0} {noun}s";

    private static string Qty(decimal quantity) => quantity.ToString("#,0.##");

    private static string Days(int days) => days switch
    {
        0 => "under a day",
        1 => "1 day",
        _ => $"{days:N0} days"
    };

    private static string Days(int days, double hours) => days >= 1 ? Days(days) : $"{Math.Max(0, hours):N0} h";

    /// <summary>
    /// A wait in the unit that reads best at its size. Null is an em dash — a van that asked for
    /// nothing has no wait, which is not the same as being served instantly.
    /// </summary>
    private static string Wait(double? hours) => hours switch
    {
        null => "—",
        < 1 => $"{Math.Max(1, Math.Round(hours.Value * 60)):N0} min",
        < 48 => $"{hours.Value:N0} h",
        _ => $"{hours.Value / 24:N1} days"
    };

    private static string SlowestTenth(double? hours, string subject) =>
        hours is null ? "nothing measured" : $"{subject} · 1 in 10 over {Wait(hours)}";
}
