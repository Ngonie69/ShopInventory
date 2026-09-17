using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor;
using ShopInventory.Web.Data;
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
/// Paging, the state counts and the search are all the API's; this page holds the filter and draws.
/// </remarks>
public partial class VanSalesInvoices : ComponentBase, IDisposable
{
    [Inject] private IMediator Mediator { get; set; } = null!;
    [Inject] private ISnackbar Snackbar { get; set; } = null!;
    [Inject] private IInvoiceService InvoiceService { get; set; } = null!;
    [Inject] private IAuditService AuditService { get; set; } = null!;
    [Inject] private IJSRuntime JS { get; set; } = null!;

    private const int PageSize = 50;

    private readonly CancellationTokenSource disposeCts = new();

    private VanSalesInvoicesResponse view = new();

    // A week to today, in CAT. The question this page is opened with is nearly always about the last few days.
    private DateTime? fromDate = IAuditService.ToCAT(DateTime.UtcNow).Date.AddDays(-6);
    private DateTime? toDate = IAuditService.ToCAT(DateTime.UtcNow).Date;
    private string searchTerm = string.Empty;
    private Guid? repFilter;
    private string? stateFilter;
    private int page = 1;

    private bool isLoading = true;
    private string? loadError;

    private string? detailReference;
    private VanSalesInvoiceDetailModel? detail;
    private bool isDetailLoading;
    private string? detailError;
    private bool isDownloading;

    private int PageCount => Math.Max(1, (int)Math.Ceiling(view.TotalCount / (double)PageSize));

    private string PageSummary => view.TotalCount == 0
        ? "Nothing to show"
        : $"{(page - 1) * PageSize + 1:N0}–{Math.Min(page * PageSize, view.TotalCount):N0} of {view.TotalCount:N0}";

    private IEnumerable<NocturneSelectOption<Guid?>> RepOptions =>
        view.Reps
            .Select(rep => new NocturneSelectOption<Guid?>(rep.UserId, rep.Name))
            .Prepend(new NocturneSelectOption<Guid?>(null, "All reps", "neutral") { RuleAfter = true });

    protected override Task OnInitializedAsync() => LoadAsync();

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

    private Task SetStateAsync(string? state)
    {
        if (stateFilter == state)
        {
            return Task.CompletedTask;
        }

        stateFilter = state;
        return ApplyAsync();
    }

    private Task GoToPageAsync(int target)
    {
        page = Math.Clamp(target, 1, PageCount);
        return LoadAsync();
    }

    private Task SearchKeyAsync(KeyboardEventArgs e) =>
        e.Key == "Enter" ? ApplyAsync() : Task.CompletedTask;

    private Task RowKeyAsync(KeyboardEventArgs e, string reference) =>
        e.Key is "Enter" or " " ? OpenAsync(reference) : Task.CompletedTask;

    private async Task LoadAsync()
    {
        if (disposeCts.IsCancellationRequested)
        {
            return;
        }

        var today = IAuditService.ToCAT(DateTime.UtcNow).Date;
        var from = (fromDate ?? today.AddDays(-6)).Date;
        var to = (toDate ?? today).Date;

        if (to < from)
        {
            loadError = "The end date is before the start date.";
            return;
        }

        isLoading = true;
        loadError = null;

        try
        {
            var result = await Mediator.Send(
                new GetVanSalesInvoicesQuery(new VanSalesDocumentFilter
                {
                    FromDate = from,
                    ToDate = to,
                    RepUserId = repFilter,
                    State = stateFilter,
                    Search = string.IsNullOrWhiteSpace(searchTerm) ? null : searchTerm.Trim(),
                    Page = page,
                    PageSize = PageSize
                }),
                disposeCts.Token);

            if (result.IsError)
            {
                // Said on the page, not only in a toast: an empty list under a failed load would otherwise read
                // as a quiet day.
                loadError = result.FirstError.Description;
                view = new VanSalesInvoicesResponse();
                return;
            }

            view = result.Value;
        }
        catch (OperationCanceledException) when (disposeCts.IsCancellationRequested)
        {
        }
        finally
        {
            isLoading = false;
        }
    }

    private async Task OpenAsync(string reference)
    {
        detailReference = reference;
        detail = null;
        detailError = null;
        isDetailLoading = true;

        try
        {
            var result = await Mediator.Send(new GetVanSalesInvoiceQuery(reference), disposeCts.Token);

            if (result.IsError)
            {
                detailError = result.FirstError.Description;
                return;
            }

            // The panel may have been closed, or another row opened, while this was loading.
            if (detailReference == reference)
            {
                detail = result.Value;
            }
        }
        catch (OperationCanceledException) when (disposeCts.IsCancellationRequested)
        {
        }
        finally
        {
            isDetailLoading = false;
        }
    }

    private void CloseDetail()
    {
        detailReference = null;
        detail = null;
        detailError = null;
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
}
