using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using ShopInventory.Web.Features.Batches.Queries.GetBatchStatusHistory;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

public partial class LabBatchStatusHistory : ComponentBase
{
    private const int PageSize = 25;

    [Inject]
    private IMediator Mediator { get; set; } = null!;

    [Inject]
    private ISnackbar Snackbar { get; set; } = null!;

    private readonly List<BatchStatusHistoryItem> historyItems = new();
    private string searchTerm = string.Empty;
    private string lastSearchTerm = string.Empty;
    private bool hasLoaded;
    private bool hasMore;
    private bool isLoading;
    private int currentPage;

    protected override async Task OnInitializedAsync()
    {
        await LoadHistoryAsync(reset: true);
    }

    private async Task SearchAsync()
    {
        await LoadHistoryAsync(reset: true);
    }

    private async Task HandleSearchKeyDown(KeyboardEventArgs args)
    {
        if (args.Key == "Enter")
        {
            await SearchAsync();
        }
    }

    private async Task LoadMoreAsync()
    {
        if (isLoading || !hasMore)
        {
            return;
        }

        await LoadHistoryAsync(reset: false);
    }

    private async Task RefreshAsync()
    {
        await LoadHistoryAsync(reset: true);
    }

    private async Task LoadHistoryAsync(bool reset)
    {
        var nextPage = reset ? 1 : currentPage + 1;
        isLoading = true;

        try
        {
            var result = await Mediator.Send(new GetBatchStatusHistoryQuery(searchTerm, nextPage, PageSize));
            if (result.IsError)
            {
                Snackbar.Add(result.FirstError.Description, Severity.Error);
                return;
            }

            if (reset)
            {
                historyItems.Clear();
            }

            historyItems.AddRange(result.Value.Items);
            currentPage = result.Value.Page;
            hasMore = result.Value.HasMore;
            lastSearchTerm = result.Value.SearchTerm;
            hasLoaded = true;
        }
        finally
        {
            isLoading = false;
        }
    }

    private static string GetStatusLabel(string? status)
        => status switch
        {
            "NotAccessible" => "Not Accessible",
            null or "" => "Unknown",
            _ => status
        };

    private static string GetStatusChipClass(string? status)
        => status switch
        {
            "Released" => "lbh-chip lbh-chip-released",
            "Locked" => "lbh-chip lbh-chip-locked",
            "NotAccessible" or "Not Accessible" => "lbh-chip lbh-chip-notaccessible",
            _ => "lbh-chip lbh-chip-unknown"
        };

    private static string GetOutcomeClass(bool isSuccess)
        => isSuccess
            ? "lbh-outcome lbh-outcome-success"
            : "lbh-outcome lbh-outcome-failed";

    private static string FormatTimestamp(DateTime timestamp)
        => $"{IAuditService.ToCAT(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)):dd MMM yyyy HH:mm:ss} CAT";
}