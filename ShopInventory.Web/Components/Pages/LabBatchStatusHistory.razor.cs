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
    private bool hasMore;
    private bool isLoading;
    private int currentPage;

    private string CountLabel
        => $"{historyItems.Count} change{(historyItems.Count == 1 ? "" : "s")} loaded"
           + (string.IsNullOrWhiteSpace(lastSearchTerm) ? "" : $" for {lastSearchTerm}");

    private string RangeLabel
        => hasMore
            ? $"Showing the {historyItems.Count} most recent changes"
            : $"All {historyItems.Count} recorded change{(historyItems.Count == 1 ? "" : "s")}";

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

    /// <summary>
    /// Status is drawn as a coloured dot plus the word, the same way batch control
    /// draws it — Released wears the accent, the rest step down the neutral ramp.
    /// </summary>
    private static string GetStatusToneClass(string? status)
        => status switch
        {
            "Released" => "is-released",
            "Locked" => "is-locked",
            "NotAccessible" or "Not Accessible" => "is-blocked",
            _ => "is-unknown"
        };

    private static string FormatTimestamp(DateTime timestamp)
        => $"{IAuditService.ToCAT(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)):dd MMM yyyy HH:mm} CAT";
}
