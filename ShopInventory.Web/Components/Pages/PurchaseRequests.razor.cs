using MediatR;
using Microsoft.AspNetCore.Components;
using ShopInventory.Web.Data;
using ShopInventory.Web.Features.PurchaseRequests.Queries.GetPurchaseRequests;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

public partial class PurchaseRequests
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IAuditService AuditService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ILogger<PurchaseRequests> Logger { get; set; } = default!;

    private PurchaseRequestListResponse? requestResponse;
    private PurchaseRequestDto? selectedRequest;
    private bool isLoading = true;
    private bool hasInitialized;
    private bool hasLoggedView;
    private string? errorMessage;
    private DateTime? fromDate = DateTime.Today.AddDays(-30);
    private DateTime? toDate = DateTime.Today;
    private int currentPage = 1;
    private const int PageSize = 20;

    private List<PurchaseRequestDto> Requests => requestResponse?.Requests ?? new List<PurchaseRequestDto>();
    private int CurrentPageCount => requestResponse?.Count ?? Requests.Count;
    private int OpenRequestCount => Requests.Count(request => string.Equals(request.DocStatus, "Open", StringComparison.OrdinalIgnoreCase));
    private int CurrentPageLineCount => Requests.Sum(request => request.Lines.Count);

    // The window the page opened on, stated in the sticky bar: the figures below
    // are all "in this window", and once the hero has scrolled away nothing else
    // says which window that is.
    private string DateRangeLabel =>
        $"{fromDate?.ToString("dd MMM yyyy") ?? "Any"} – {toDate?.ToString("dd MMM yyyy") ?? "Any"}";

    private string RegisterCountText => requestResponse is null
        ? "Not loaded"
        : $"{CurrentPageCount:N0} of {requestResponse.TotalCount:N0}";

    // SAP is asked for one page at a time, so the foot counts the rows on screen
    // against the whole result rather than numbering pages it has not seen.
    private string PageRangeText
    {
        get
        {
            if (CurrentPageCount == 0)
                return "No requests";

            var start = ((currentPage - 1) * PageSize) + 1;
            return $"Showing {start:N0}–{start + CurrentPageCount - 1:N0} of {requestResponse?.TotalCount ?? CurrentPageCount:N0}";
        }
    }

    // SAP carries the requester as a name where it has one and as a bare user id
    // where it does not; the register says whichever exists rather than showing
    // an empty column.
    private static string RequesterOf(PurchaseRequestDto request) =>
        string.IsNullOrWhiteSpace(request.RequesterName)
            ? request.Requester?.ToString() ?? "Not set"
            : request.RequesterName;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || hasInitialized)
            return;

        hasInitialized = true;
        await LoadRequestsAsync();
        StateHasChanged();
    }

    private async Task LoadRequestsAsync()
    {
        isLoading = true;
        errorMessage = null;

        try
        {
            var result = await Mediator.Send(new GetPurchaseRequestsQuery(currentPage, PageSize, fromDate, toDate));

            result.SwitchFirst(
                value =>
                {
                    requestResponse = value;
                    // Cleared rather than set to the first row: the detail is a
                    // drawer now, and pre-selecting would open it over the
                    // register on every load and every page turn.
                    selectedRequest = null;
                },
                error =>
                {
                    requestResponse = new PurchaseRequestListResponse
                    {
                        Page = currentPage,
                        PageSize = PageSize
                    };
                    selectedRequest = null;
                    errorMessage = error.Description;
                });

            if (!result.IsError && !hasLoggedView)
            {
                hasLoggedView = true;
                await AuditService.LogAsync(AuditActions.ViewPurchaseRequests, "PurchaseRequest", null);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load purchase requests page");
            requestResponse = new PurchaseRequestListResponse
            {
                Page = currentPage,
                PageSize = PageSize
            };
            selectedRequest = null;
            errorMessage = "Failed to load purchase requests.";
        }
        finally
        {
            isLoading = false;
        }
    }

    private void SelectRequest(PurchaseRequestDto request)
    {
        selectedRequest = request;
    }

    private void CloseDetail() => selectedRequest = null;

    private async Task SearchAsync()
    {
        currentPage = 1;
        await LoadRequestsAsync();
    }

    private async Task ClearFiltersAsync()
    {
        fromDate = DateTime.Today.AddDays(-30);
        toDate = DateTime.Today;
        currentPage = 1;
        await LoadRequestsAsync();
    }

    private async Task ReloadAsync()
    {
        await LoadRequestsAsync();
    }

    private async Task PreviousPageAsync()
    {
        if (currentPage <= 1)
            return;

        currentPage--;
        await LoadRequestsAsync();
    }

    private async Task NextPageAsync()
    {
        if (!(requestResponse?.HasMore ?? false))
            return;

        currentPage++;
        await LoadRequestsAsync();
    }

    private void NavigateToCreate()
    {
        NavigationManager.NavigateTo("/purchase-requests/create");
    }

    // The badge takes its hue from a family token in purchase-documents.css, so
    // what this returns is the family class rather than a status name: open is
    // the good family, closed the neutral one, cancelled the bad one.
    private static string GetStatusCssClass(string? status)
    {
        if (string.Equals(status, "Closed", StringComparison.OrdinalIgnoreCase))
            return "pdx-fam-neutral";
        if (string.Equals(status, "Cancelled", StringComparison.OrdinalIgnoreCase))
            return "pdx-fam-bad";
        return "pdx-fam-good";
    }
}