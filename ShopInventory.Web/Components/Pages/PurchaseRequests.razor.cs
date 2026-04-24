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
                    selectedRequest = value.Requests.FirstOrDefault();
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

    private static string GetStatusCssClass(string? status)
    {
        if (string.Equals(status, "Closed", StringComparison.OrdinalIgnoreCase))
            return "closed";
        if (string.Equals(status, "Cancelled", StringComparison.OrdinalIgnoreCase))
            return "cancelled";
        return "open";
    }
}