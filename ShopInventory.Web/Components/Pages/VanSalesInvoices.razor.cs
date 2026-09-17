using System.Globalization;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor;
using ShopInventory.Web.Common;
using ShopInventory.Web.Data;
using ShopInventory.Web.Features.VanSalesDocuments;
using ShopInventory.Web.Features.VanSalesDocuments.Queries.GetVanSalesInvoice;
using ShopInventory.Web.Features.VanSalesDocuments.Queries.GetVanSalesInvoices;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

/// <summary>
/// The invoices the van sales app created, and where each stands at ZIMRA and in SAP.
/// </summary>
/// <remarks>
/// The list is read from the API's own records of van sales rather than from SAP, so a sale that has been
/// fiscalised and not yet invoiced is on it — which is exactly the sale someone has to be able to find.
/// Paging, the state counts, the summary and the search are all the API's; this page holds the filter and
/// draws.
/// </remarks>
public partial class VanSalesInvoices : ComponentBase, IDisposable
{
    [Inject] private IMediator Mediator { get; set; } = null!;
    [Inject] private ISnackbar Snackbar { get; set; } = null!;
    [Inject] private IInvoiceService InvoiceService { get; set; } = null!;
    [Inject] private IAuditService AuditService { get; set; } = null!;
    [Inject] private IReportExportService ExportService { get; set; } = null!;
    [Inject] private IJSRuntime JS { get; set; } = null!;

    /// <summary>Opens the list already searched — the credit notes drawer links here by van order.</summary>
    [SupplyParameterFromQuery(Name = "search")] public string? SearchQuery { get; set; }

    /// <summary>With <see cref="ToQuery"/>, opens the list on a custom period.</summary>
    [SupplyParameterFromQuery(Name = "from")] public string? FromQuery { get; set; }

    [SupplyParameterFromQuery(Name = "to")] public string? ToQuery { get; set; }

    /// <summary>A van order whose drawer opens once the page has loaded.</summary>
    [SupplyParameterFromQuery(Name = "open")] public string? OpenQuery { get; set; }

    private const int PageSize = 50;

    /// <summary>The API's own ceiling on a page, which the export walks the result set in.</summary>
    private const int ExportPageSize = 200;

    private const string PeriodToday = "today";
    private const string PeriodWeek = "7";
    private const string PeriodMonth = "30";
    private const string PeriodCustom = "custom";

    private static readonly (string Value, string Label)[] PeriodOptions =
    [
        (PeriodToday, "Today"),
        (PeriodWeek, "7 days"),
        (PeriodMonth, "30 days"),
        (PeriodCustom, "Custom")
    ];

    private static readonly (string? Value, string Label)[] ChannelOptions =
    [
        (null, "All"),
        ("Online", "Online"),
        ("Offline", "Offline")
    ];

    private readonly CancellationTokenSource disposeCts = new();

    private VanSalesInvoicesResponse view = new();

    // A week to today, in CAT. The question this page is opened with is nearly always about the last few days.
    private string period = PeriodWeek;
    private DateTime? fromDate = Today.AddDays(-6);
    private DateTime? toDate = Today;
    private string searchTerm = string.Empty;
    private Guid? repFilter;
    private string? channelFilter;
    private string? stateFilter;
    private int page = 1;

    private bool isLoading = true;
    private bool isExporting;
    private string? loadError;
    private DateTime? readAt;

    private string? detailReference;
    private VanSalesInvoiceDetailModel? detail;
    private bool isDetailLoading;
    private string? detailError;
    private bool isDownloading;

    private static DateTime Today => IAuditService.ToCAT(DateTime.UtcNow).Date;

    private int PageCount => Math.Max(1, (int)Math.Ceiling(view.TotalCount / (double)PageSize));

    private string PageSummary => view.TotalCount == 0
        ? "Nothing to show"
        : $"Showing {(page - 1) * PageSize + 1:N0}–{Math.Min(page * PageSize, view.TotalCount):N0} of {view.TotalCount:N0}";

    private IEnumerable<NocturneSelectOption<Guid?>> RepOptions =>
        view.Reps
            .Select(rep => new NocturneSelectOption<Guid?>(rep.UserId, rep.Name))
            .Prepend(new NocturneSelectOption<Guid?>(null, "All reps", "neutral") { RuleAfter = true });

    private string PeriodLabel => period switch
    {
        PeriodToday => "Today",
        PeriodWeek => "Last 7 days",
        PeriodMonth => "Last 30 days",
        _ => VanSalesDocumentDisplay.Range(fromDate ?? Today, toDate ?? Today)
    };

    /// <summary>The row the drawer is showing, when it is on the page that is loaded.</summary>
    private int DetailIndex => detailReference is null
        ? -1
        : view.Rows.FindIndex(row => row.Reference == detailReference);

    private decimal NotInSapMax => view.Summary.NotInSapByVan.Select(van => van.Amount).DefaultIfEmpty(0m).Max();

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

        return LoadAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Once, after the first load: the drawer is opened over the list it was linked to, not before it.
        if (!isLoading && !string.IsNullOrWhiteSpace(OpenQuery))
        {
            var reference = OpenQuery.Trim();
            OpenQuery = null;
            await OpenAsync(reference);
            StateHasChanged();
        }
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
            case PeriodToday:
                fromDate = Today;
                toDate = Today;
                break;
            case PeriodWeek:
                fromDate = Today.AddDays(-6);
                toDate = Today;
                break;
            case PeriodMonth:
                fromDate = Today.AddDays(-29);
                toDate = Today;
                break;
            default:
                // Custom opens on whatever was showing, so choosing it changes nothing until a date does.
                return Task.CompletedTask;
        }

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

    private Task SetChannelAsync(string? channel)
    {
        if (channelFilter == channel)
        {
            return Task.CompletedTask;
        }

        channelFilter = channel;
        return ApplyAsync();
    }

    /// <summary>A lane is the state filter: pressing the one that is on turns it off again.</summary>
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

    private Task RowKeyAsync(KeyboardEventArgs e, string reference) =>
        e.Key is "Enter" or " " ? OpenAsync(reference) : Task.CompletedTask;

    private VanSalesDocumentFilter Filter(int pageNumber, int pageSize) => new()
    {
        FromDate = (fromDate ?? Today.AddDays(-6)).Date,
        ToDate = (toDate ?? Today).Date,
        RepUserId = repFilter,
        Channel = channelFilter,
        State = stateFilter,
        Search = string.IsNullOrWhiteSpace(searchTerm) ? null : searchTerm.Trim(),
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
            var result = await Mediator.Send(new GetVanSalesInvoicesQuery(Filter(page, PageSize)), disposeCts.Token);

            if (result.IsError)
            {
                // Said on the page, not only in a toast: an empty list under a failed load would otherwise read
                // as a quiet day.
                loadError = result.FirstError.Description;
                view = new VanSalesInvoicesResponse();
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

    private async Task OpenAsync(string reference)
    {
        detailReference = reference;
        detail = null;
        detailError = null;
        isDetailLoading = true;

        try
        {
            var result = await Mediator.Send(new GetVanSalesInvoiceQuery(reference), disposeCts.Token);

            // The drawer may have been closed, or stepped to another invoice, while this was loading.
            if (detailReference != reference)
            {
                return;
            }

            if (result.IsError)
            {
                detailError = result.FirstError.Description;
                return;
            }

            detail = result.Value;
        }
        catch (OperationCanceledException) when (disposeCts.IsCancellationRequested)
        {
        }
        finally
        {
            if (detailReference == reference)
            {
                isDetailLoading = false;
            }
        }
    }

    /// <summary>
    /// Walks the page that is loaded, not the whole result set: a step that fetched the next page would move
    /// the list under the drawer while it was being read.
    /// </summary>
    private Task StepAsync(int direction)
    {
        var index = DetailIndex;

        if (index < 0 || view.Rows.Count < 2)
        {
            return Task.CompletedTask;
        }

        var next = (index + direction + view.Rows.Count) % view.Rows.Count;
        return OpenAsync(view.Rows[next].Reference);
    }

    private Task DrawerKeyAsync(KeyboardEventArgs e)
    {
        switch (e.Key)
        {
            case "Escape":
                CloseDetail();
                return Task.CompletedTask;
            case "ArrowUp":
                return StepAsync(-1);
            case "ArrowDown":
                return StepAsync(1);
            default:
                return Task.CompletedTask;
        }
    }

    private void CloseDetail()
    {
        detailReference = null;
        detail = null;
        detailError = null;
        isDetailLoading = false;
    }

    private async Task DownloadPdfAsync()
    {
        if (detail?.Invoice is not { SapDocEntry: { } docEntry } invoice || isDownloading)
        {
            return;
        }

        isDownloading = true;

        try
        {
            var pdf = await InvoiceService.GetInvoicePdfAsync(docEntry, detail.FiscalQrCode);

            if (pdf is null || pdf.Length == 0)
            {
                Snackbar.Add("SAP did not return a PDF for this invoice.", Severity.Error);
                return;
            }

            await JS.InvokeVoidAsync(
                "downloadPdfFromBase64",
                Convert.ToBase64String(pdf),
                $"Invoice_{invoice.SapDocNum?.ToString() ?? invoice.Reference}.pdf");

            await AuditService.LogAsync(AuditActions.DownloadInvoicePdf, "Invoice", invoice.SapDocNum?.ToString());
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Download failed: {ex.Message}", Severity.Error);
        }
        finally
        {
            isDownloading = false;
        }
    }

    // ── Export ──────────────────────────────────────────────────────────

    /// <summary>
    /// Every invoice the filters match, not only the page on screen — a workbook of fifty rows out of two
    /// hundred would be taken for the period.
    /// </summary>
    private async Task ExportAsync()
    {
        if (isExporting)
        {
            return;
        }

        isExporting = true;

        try
        {
            var rows = new List<VanSalesInvoiceRowModel>();

            for (var pageNumber = 1; ; pageNumber++)
            {
                var result = await Mediator.Send(
                    new GetVanSalesInvoicesQuery(Filter(pageNumber, ExportPageSize)), disposeCts.Token);

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
            var bytes = ExportService.ExportVanSalesInvoicesToExcel(rows, filter.FromDate, filter.ToDate, FilterDescription());
            var fileName = $"VanSalesInvoices_{IAuditService.ToCAT(DateTime.UtcNow):yyyyMMdd_HHmmss}.xlsx";

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

    /// <summary>What narrowed the export, said on the sheet so it is not read as the whole period.</summary>
    private string? FilterDescription()
    {
        var parts = new List<string>();

        if (repFilter is { } rep)
        {
            parts.Add($"Rep: {view.Reps.FirstOrDefault(r => r.UserId == rep)?.Name ?? "one rep"}");
        }

        if (channelFilter is not null)
        {
            parts.Add($"{channelFilter} only");
        }

        if (stateFilter is not null)
        {
            parts.Add(VanSalesDocumentDisplay.Label(stateFilter));
        }

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            parts.Add($"Search: \"{searchTerm.Trim()}\"");
        }

        return parts.Count == 0 ? null : string.Join("  |  ", parts);
    }

    // ── Words ───────────────────────────────────────────────────────────

    private static DateTime? TryParseDay(string? value) =>
        DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day.Date
            : null;

    private static string Stamp(DateTime utc) =>
        IAuditService.ToCAT(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToString("dd MMM HH:mm", CultureInfo.InvariantCulture);

    private static string Full(DateTime utc) =>
        IAuditService.ToCAT(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToString("dd MMM yyyy, HH:mm", CultureInfo.InvariantCulture);

    /// <summary>The fiscal cell: the receipt when there is one, and otherwise what its absence means.</summary>
    private static (string Text, string Family) FiscalMark(VanSalesInvoiceRowModel row) =>
        !string.IsNullOrWhiteSpace(row.FiscalReceiptNumber)
            ? (row.FiscalReceiptNumber, "vsd-fam-good")
            : row.State switch
            {
                VanSalesDocumentState.NotFiscalised => ("No receipt", "vsd-fam-warn"),
                VanSalesDocumentState.NeedsAttention => ("Not signed", "vsd-fam-bad"),
                _ => ("Pending", "vsd-fam-neutral")
            };

    private static (string Text, string Family) SapMark(VanSalesInvoiceRowModel row) =>
        row.SapDocNum is { } docNum
            ? ($"#{docNum}", "vsd-fam-good")
            : row.State == VanSalesDocumentState.NeedsAttention && !string.IsNullOrWhiteSpace(row.FiscalReceiptNumber)
                ? ("Refused", "vsd-fam-bad")
                : ("Not yet", "vsd-fam-off");

    private static string Tender(string? paymentMethod) =>
        string.IsNullOrWhiteSpace(paymentMethod) ? "Not recorded" : paymentMethod.Trim();

    /// <summary>The trail the drawer ends on: sold, signed, invoiced.</summary>
    private static List<VanSalesTrailStep> Trail(VanSalesInvoiceDetailModel detail)
    {
        var invoice = detail.Invoice;
        var signed = !string.IsNullOrWhiteSpace(invoice.FiscalReceiptNumber);
        var inSap = invoice.SapDocNum is not null;
        var failed = !string.IsNullOrWhiteSpace(invoice.Problem);

        var steps = new List<VanSalesTrailStep>
        {
            new("Sold on the handset",
                string.Join(" · ", new[] { Full(invoice.CreatedAtUtc), invoice.Channel.ToLowerInvariant(), invoice.WarehouseCode }
                    .Where(part => !string.IsNullOrWhiteSpace(part))),
                "vsd-fam-good"),

            signed
                ? new("Fiscalised by ZIMRA",
                    $"Receipt {invoice.FiscalReceiptNumber}" + (invoice.FiscalDay is null ? "" : $" · fiscal day {invoice.FiscalDay}"),
                    "vsd-fam-good")
                : failed && !inSap
                    ? new("Not fiscalised", "The fiscal device has not confirmed a receipt", "vsd-fam-bad")
                    : new("Waiting for a fiscal receipt", inSap ? "None found for the SAP invoice" : "Not signed yet",
                        inSap ? "vsd-fam-warn" : "vsd-fam-off"),

            inSap
                ? new("Invoiced in SAP", $"Invoice #{invoice.SapDocNum}", "vsd-fam-good")
                : failed && signed
                    ? new("Sent to SAP",
                        detail.PostingAttempts > 0
                            ? $"Refused {detail.PostingAttempts} time{(detail.PostingAttempts == 1 ? "" : "s")}"
                            : "Refused",
                        "vsd-fam-bad")
                    : new("Not in SAP yet",
                        string.IsNullOrWhiteSpace(detail.QueueStatus) ? "Waiting to be posted" : $"Queue entry {detail.QueueStatus}",
                        "vsd-fam-off")
        };

        return VanSalesTrailStep.MarkCurrent(steps);
    }
}
