using System.Globalization;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor;
using ShopInventory.Web.Features.VanSalesDocuments;
using ShopInventory.Web.Features.VanSalesDocuments.Queries.GetVanSalesCreditNotes;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

/// <summary>
/// Credit notes raised against invoices the van sales app created.
/// </summary>
/// <remarks>
/// The handset raises none of its own, so this is the office's side of a van sale: every credit that
/// reverses one, with the sale it reverses beside it. Paging, counts, the summary and search are the API's.
/// The drawer is drawn from the list row, which carries everything it states — there is no detail call.
/// </remarks>
public partial class VanSalesCreditNotes : ComponentBase, IDisposable
{
    [Inject] private IMediator Mediator { get; set; } = null!;
    [Inject] private ISnackbar Snackbar { get; set; } = null!;
    [Inject] private IReportExportService ExportService { get; set; } = null!;
    [Inject] private IJSRuntime JS { get; set; } = null!;

    /// <summary>Opens the list already searched — the invoice drawer links here by credit note number.</summary>
    [SupplyParameterFromQuery(Name = "search")] public string? SearchQuery { get; set; }

    /// <summary>With <see cref="ToQuery"/>, opens the list on a custom period.</summary>
    [SupplyParameterFromQuery(Name = "from")] public string? FromQuery { get; set; }

    [SupplyParameterFromQuery(Name = "to")] public string? ToQuery { get; set; }

    /// <summary>A credit note key whose drawer opens once the page has loaded.</summary>
    [SupplyParameterFromQuery(Name = "open")] public string? OpenQuery { get; set; }

    private const int PageSize = 50;
    private const int ExportPageSize = 200;

    private const string PeriodWeek = "7";
    private const string PeriodMonth = "30";
    private const string PeriodQuarter = "90";
    private const string PeriodCustom = "custom";

    private static readonly (string Value, string Label)[] PeriodOptions =
    [
        (PeriodWeek, "7 days"),
        (PeriodMonth, "30 days"),
        (PeriodQuarter, "90 days"),
        (PeriodCustom, "Custom")
    ];

    private readonly CancellationTokenSource disposeCts = new();

    private VanSalesCreditNotesResponse view = new();

    // A month to today, in CAT. Credits trail the sales they reverse, so a week would miss most of them.
    private string period = PeriodMonth;
    private DateTime? fromDate = Today.AddDays(-29);
    private DateTime? toDate = Today;
    private string searchTerm = string.Empty;
    private string? originFilter;

    // Off by default: a cancelled memo gave nothing back, and the summary already says how many there are.
    private bool showCancelled;
    private string? stateFilter;
    private int page = 1;

    private bool isLoading = true;
    private bool isExporting;
    private string? loadError;
    private DateTime? readAt;

    private string? openKey;

    private static DateTime Today => IAuditService.ToCAT(DateTime.UtcNow).Date;

    private int PageCount => Math.Max(1, (int)Math.Ceiling(view.TotalCount / (double)PageSize));

    private string PageSummary => view.TotalCount == 0
        ? "Nothing to show"
        : $"Showing {(page - 1) * PageSize + 1:N0}–{Math.Min(page * PageSize, view.TotalCount):N0} of {view.TotalCount:N0}";

    private string PeriodLabel => period switch
    {
        PeriodWeek => "Last 7 days",
        PeriodMonth => "Last 30 days",
        PeriodQuarter => "Last 90 days",
        _ => VanSalesDocumentDisplay.Range(fromDate ?? Today, toDate ?? Today)
    };

    private VanSalesCreditNoteRowModel? Open => openKey is null ? null : view.Rows.FirstOrDefault(row => row.Key == openKey);

    private int OpenIndex => openKey is null ? -1 : view.Rows.FindIndex(row => row.Key == openKey);

    private decimal TopCustomerMax => view.Summary.TopCustomers.Select(c => c.Amount).DefaultIfEmpty(0m).Max();

    private (string? Value, string Label, int? Count)[] OriginOptions =>
    [
        (null, "All", null),
        ("SAP", "SAP", view.Summary.Sap),
        ("Till", "Till", view.Summary.Till)
    ];

    protected override Task OnInitializedAsync()
    {
        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            searchTerm = SearchQuery.Trim();
        }

        if (TryParseDay(FromQuery) is { } from && TryParseDay(ToQuery) is { } to && to >= from)
        {
            period = PeriodCustom;
            fromDate = from;
            toDate = to;
        }

        if (!string.IsNullOrWhiteSpace(OpenQuery))
        {
            // Linked to one note: it is shown even if it was cancelled, or the link would open onto nothing.
            openKey = OpenQuery.Trim();
            showCancelled = true;
        }

        return LoadAsync();
    }

    public void Dispose()
    {
        disposeCts.Cancel();
        disposeCts.Dispose();
    }

    private Task ApplyAsync()
    {
        page = 1;
        return LoadAsync();
    }

    private Task SetPeriodAsync(string value)
    {
        period = value;

        switch (value)
        {
            case PeriodWeek:
                fromDate = Today.AddDays(-6);
                break;
            case PeriodMonth:
                fromDate = Today.AddDays(-29);
                break;
            case PeriodQuarter:
                // 90 days, inside the API's 93-day ceiling on a period.
                fromDate = Today.AddDays(-89);
                break;
            default:
                return Task.CompletedTask;
        }

        toDate = Today;
        return ApplyAsync();
    }

    private Task OnFromChangedAsync(DateTime? value)
    {
        fromDate = value;
        return ApplyAsync();
    }

    private Task OnToChangedAsync(DateTime? value)
    {
        toDate = value;
        return ApplyAsync();
    }

    private Task SetOriginAsync(string? origin)
    {
        if (originFilter == origin)
        {
            return Task.CompletedTask;
        }

        originFilter = origin;
        return ApplyAsync();
    }

    private Task ToggleCancelledAsync(ChangeEventArgs e)
    {
        showCancelled = e.Value is true;
        return ApplyAsync();
    }

    private Task ToggleStateAsync(string state)
    {
        stateFilter = stateFilter == state ? null : state;
        return ApplyAsync();
    }

    private Task ClearStateAsync()
    {
        stateFilter = null;
        return ApplyAsync();
    }

    private Task GoToPageAsync(int target)
    {
        page = Math.Clamp(target, 1, PageCount);
        return LoadAsync();
    }

    private VanSalesDocumentFilter Filter(int pageNumber, int pageSize) => new()
    {
        FromDate = (fromDate ?? Today.AddDays(-29)).Date,
        ToDate = (toDate ?? Today).Date,
        State = stateFilter,
        Search = string.IsNullOrWhiteSpace(searchTerm) ? null : searchTerm.Trim(),
        Origin = originFilter,
        IncludeCancelled = showCancelled,
        Page = pageNumber,
        PageSize = pageSize
    };

    private async Task LoadAsync()
    {
        if (disposeCts.IsCancellationRequested)
        {
            return;
        }

        if ((toDate ?? Today).Date < (fromDate ?? Today).Date)
        {
            loadError = "The end date is before the start date.";
            isLoading = false;
            return;
        }

        isLoading = true;
        loadError = null;

        try
        {
            var result = await Mediator.Send(new GetVanSalesCreditNotesQuery(Filter(page, PageSize)), disposeCts.Token);

            if (result.IsError)
            {
                loadError = result.FirstError.Description;
                view = new VanSalesCreditNotesResponse();
                return;
            }

            view = result.Value;
            readAt = IAuditService.ToCAT(DateTime.UtcNow);
        }
        catch (OperationCanceledException) when (disposeCts.IsCancellationRequested)
        {
        }
        finally
        {
            isLoading = false;
        }
    }

    // ── The drawer ──────────────────────────────────────────────────────

    private void OpenNote(string key) => openKey = key;

    private void CloseNote() => openKey = null;

    private Task RowKeyAsync(KeyboardEventArgs e, string key)
    {
        if (e.Key is "Enter" or " ")
        {
            OpenNote(key);
        }

        return Task.CompletedTask;
    }

    /// <summary>Walks the page that is loaded, as the invoices drawer does.</summary>
    private void Step(int direction)
    {
        var index = OpenIndex;

        if (index < 0 || view.Rows.Count < 2)
        {
            return;
        }

        openKey = view.Rows[(index + direction + view.Rows.Count) % view.Rows.Count].Key;
    }

    private void DrawerKey(KeyboardEventArgs e)
    {
        switch (e.Key)
        {
            case "Escape":
                CloseNote();
                break;
            case "ArrowUp":
                Step(-1);
                break;
            case "ArrowDown":
                Step(1);
                break;
        }
    }

    // ── Export ──────────────────────────────────────────────────────────

    private async Task ExportAsync()
    {
        if (isExporting)
        {
            return;
        }

        isExporting = true;

        try
        {
            var rows = new List<VanSalesCreditNoteRowModel>();

            for (var pageNumber = 1; ; pageNumber++)
            {
                var result = await Mediator.Send(
                    new GetVanSalesCreditNotesQuery(Filter(pageNumber, ExportPageSize)), disposeCts.Token);

                if (result.IsError)
                {
                    Snackbar.Add($"The export could not be read: {result.FirstError.Description}", Severity.Error);
                    return;
                }

                rows.AddRange(result.Value.Rows);

                if (result.Value.Rows.Count < ExportPageSize || rows.Count >= result.Value.TotalCount)
                {
                    break;
                }
            }

            var filter = Filter(1, ExportPageSize);
            var bytes = ExportService.ExportVanSalesCreditNotesToExcel(rows, filter.FromDate, filter.ToDate, FilterDescription());
            var fileName = $"VanSalesCreditNotes_{IAuditService.ToCAT(DateTime.UtcNow):yyyyMMdd_HHmmss}.xlsx";

            await JS.InvokeVoidAsync("downloadFile", fileName, Convert.ToBase64String(bytes));
        }
        catch (OperationCanceledException) when (disposeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Snackbar.Add($"The export could not be downloaded: {ex.Message}", Severity.Error);
        }
        finally
        {
            isExporting = false;
        }
    }

    private string? FilterDescription()
    {
        var parts = new List<string>();

        if (originFilter is not null)
        {
            parts.Add($"Raised in {originFilter} only");
        }

        parts.Add(showCancelled ? "Cancelled notes listed" : "Cancelled notes left out");

        if (stateFilter is not null)
        {
            parts.Add(VanSalesDocumentDisplay.Label(stateFilter));
        }

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            parts.Add($"Search: \"{searchTerm.Trim()}\"");
        }

        return string.Join("  |  ", parts);
    }

    // ── Words ───────────────────────────────────────────────────────────

    private static DateTime? TryParseDay(string? value) =>
        DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day.Date
            : null;

    private static string OriginWords(VanSalesCreditNoteRowModel note) =>
        note.Origin == "Till" ? "Till credit" : "SAP memo";

    private static (string Text, string Family) FiscalMark(VanSalesCreditNoteRowModel note) =>
        !string.IsNullOrWhiteSpace(note.FiscalReceiptNumber)
            ? (note.FiscalReceiptNumber, "vsd-fam-good")
            : note.IsCancelled
                ? ("—", "vsd-fam-off")
                : note.State == VanSalesDocumentState.NeedsAttention
                    ? ("Not signed", "vsd-fam-bad")
                    : ("No receipt", "vsd-fam-warn");

    private static (string Text, string Family) SapMark(VanSalesCreditNoteRowModel note) =>
        note.SapDocNum is { } docNum
            ? ($"memo #{docNum}", note.IsCancelled ? "vsd-fam-off" : "vsd-fam-good")
            : note.State == VanSalesDocumentState.NeedsAttention && !string.IsNullOrWhiteSpace(note.FiscalReceiptNumber)
                ? ("refused", "vsd-fam-bad")
                : ("not yet", "vsd-fam-off");

    /// <summary>
    /// Raised, signed, in SAP — in the order they happen for each origin. A memo is raised in SAP and then
    /// signed; a till credit is signed at the till and then reaches SAP.
    /// </summary>
    private static List<VanSalesTrailStep> Trail(VanSalesCreditNoteRowModel note)
    {
        var signed = !string.IsNullOrWhiteSpace(note.FiscalReceiptNumber);
        var failed = !string.IsNullOrWhiteSpace(note.Problem);
        var invoice = note.CreditedInvoices.FirstOrDefault();

        var steps = new List<VanSalesTrailStep>();

        if (invoice is not null)
        {
            steps.Add(new(
                "Invoice sold on the van",
                string.Join(" · ", new[] { invoice.Reference, invoice.SoldOn?.ToString("dd MMM yyyy", CultureInfo.InvariantCulture) }
                    .Where(part => !string.IsNullOrWhiteSpace(part))),
                "vsd-fam-good"));
        }

        var fiscal = signed
            ? new VanSalesTrailStep("Fiscalised by ZIMRA", $"Receipt {note.FiscalReceiptNumber}", "vsd-fam-good")
            : note.IsCancelled
                ? new VanSalesTrailStep("Not fiscalised", "Cancelled before it was signed", "vsd-fam-off")
                : failed && note.Origin == "Till"
                    ? new VanSalesTrailStep("Not fiscalised", "The device has not confirmed it", "vsd-fam-bad")
                    : new VanSalesTrailStep("Waiting for a fiscal receipt", "None found yet", "vsd-fam-warn");

        var sap = note.SapDocNum is { } docNum
            ? new VanSalesTrailStep(
                note.IsCancelled ? "Cancelled in SAP" : "Credit memo in SAP",
                $"Memo #{docNum}",
                note.IsCancelled ? "vsd-fam-neutral" : "vsd-fam-good")
            : failed && signed
                ? new VanSalesTrailStep("Sent to SAP", "Refused", "vsd-fam-bad")
                : new VanSalesTrailStep("Not in SAP yet", "Waiting to be posted", "vsd-fam-off");

        if (note.Origin == "Till")
        {
            steps.Add(new("Credit raised at the till", note.Date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture), "vsd-fam-good"));
            steps.Add(fiscal);
            steps.Add(sap);
        }
        else
        {
            var raised = $"Memo #{note.SapDocNum} · {note.Date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)}";
            steps.Add(note.IsCancelled
                ? new("Credit memo cancelled in SAP", raised, "vsd-fam-neutral")
                : new("Credit memo raised in SAP", raised, "vsd-fam-good"));
            steps.Add(fiscal);
        }

        return VanSalesTrailStep.MarkCurrent(steps);
    }

    private static string InvoiceLink(VanSalesCreditedInvoiceModel invoice)
    {
        var link = $"/van-sales/invoices?search={Uri.EscapeDataString(invoice.Reference)}&open={Uri.EscapeDataString(invoice.Reference)}";

        return invoice.SoldOn is { } soldOn
            ? $"{link}&from={soldOn:yyyy-MM-dd}&to={soldOn:yyyy-MM-dd}"
            : link;
    }
}
