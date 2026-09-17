using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using ShopInventory.Web.Features.VanSalesDocuments.Queries.GetVanSalesCreditNotes;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

/// <summary>
/// Credit notes raised against invoices the van sales app created.
/// </summary>
/// <remarks>
/// The handset raises none of its own, so this is the office's side of a van sale: every credit that
/// reverses one, with the sale it reverses beside it. Paging, counts and search are the API's.
/// </remarks>
public partial class VanSalesCreditNotes : ComponentBase, IDisposable
{
    [Inject] private IMediator Mediator { get; set; } = null!;

    private const int PageSize = 50;

    private readonly CancellationTokenSource disposeCts = new();

    private VanSalesCreditNotesResponse view = new();

    // A month to today, in CAT. Credits trail the sales they reverse, so a week would miss most of them.
    private DateTime? fromDate = IAuditService.ToCAT(DateTime.UtcNow).Date.AddDays(-30);
    private DateTime? toDate = IAuditService.ToCAT(DateTime.UtcNow).Date;
    private string searchTerm = string.Empty;
    private string? stateFilter;
    private int page = 1;

    private bool isLoading = true;
    private string? loadError;

    private int PageCount => Math.Max(1, (int)Math.Ceiling(view.TotalCount / (double)PageSize));

    private string PageSummary => view.TotalCount == 0
        ? "Nothing to show"
        : $"{(page - 1) * PageSize + 1:N0}–{Math.Min(page * PageSize, view.TotalCount):N0} of {view.TotalCount:N0}";

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

    private async Task LoadAsync()
    {
        if (disposeCts.IsCancellationRequested)
        {
            return;
        }

        var today = IAuditService.ToCAT(DateTime.UtcNow).Date;
        var from = (fromDate ?? today.AddDays(-30)).Date;
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
                new GetVanSalesCreditNotesQuery(new VanSalesDocumentFilter
                {
                    FromDate = from,
                    ToDate = to,
                    State = stateFilter,
                    Search = string.IsNullOrWhiteSpace(searchTerm) ? null : searchTerm.Trim(),
                    Page = page,
                    PageSize = PageSize
                }),
                disposeCts.Token);

            if (result.IsError)
            {
                loadError = result.FirstError.Description;
                view = new VanSalesCreditNotesResponse();
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
}
