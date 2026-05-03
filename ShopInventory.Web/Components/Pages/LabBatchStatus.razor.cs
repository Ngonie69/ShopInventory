using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using ShopInventory.Web.Features.Batches.Commands.UpdateBatchStatus;
using ShopInventory.Web.Features.Batches.Queries.SearchBatches;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Components.Pages;

public partial class LabBatchStatus : ComponentBase
{
    private static readonly IReadOnlyList<string> StatusOptions = ["Released", "Locked", "Not Accessible"];

    [Inject]
    private IMediator Mediator { get; set; } = null!;

    [Inject]
    private ISnackbar Snackbar { get; set; } = null!;

    private readonly HashSet<int> updatingBatchIds = new();
    private readonly List<BatchSearchItem> results = new();
    private string searchTerm = string.Empty;
    private string lastSearchTerm = string.Empty;
    private bool hasSearched;
    private bool isSearching;

    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            Snackbar.Add("Enter at least 2 characters to search for a batch.", Severity.Warning);
            return;
        }

        isSearching = true;

        try
        {
            var result = await Mediator.Send(new SearchBatchesQuery(searchTerm));
            if (result.IsError)
            {
                Snackbar.Add(result.FirstError.Description, Severity.Error);
                return;
            }

            results.Clear();
            results.AddRange(result.Value.Results);
            lastSearchTerm = result.Value.SearchTerm;
            hasSearched = true;
        }
        finally
        {
            isSearching = false;
        }
    }

    private async Task HandleSearchKeyDown(KeyboardEventArgs args)
    {
        if (args.Key == "Enter")
        {
            await SearchAsync();
        }
    }

    private bool IsUpdating(int batchEntryId)
        => updatingBatchIds.Contains(batchEntryId);

    private async Task UpdateStatusAsync(BatchSearchItem item)
    {
        if (string.IsNullOrWhiteSpace(item.PendingStatus) || string.Equals(item.PendingStatus, item.Status, StringComparison.Ordinal))
        {
            return;
        }

        updatingBatchIds.Add(item.BatchEntryId);

        try
        {
            var result = await Mediator.Send(new UpdateBatchStatusCommand(
                item.BatchEntryId,
                item.PendingStatus,
                item.BatchNumber,
                item.ItemCode));

            if (result.IsError)
            {
                Snackbar.Add(result.FirstError.Description, Severity.Error);
                item.PendingStatus = item.Status;
                return;
            }

            item.Status = item.PendingStatus;
            Snackbar.Add($"Batch {item.BatchNumber} updated to {item.Status}.", Severity.Success);
        }
        finally
        {
            updatingBatchIds.Remove(item.BatchEntryId);
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
            "Released" => "lbs-chip lbs-chip-released",
            "Locked" => "lbs-chip lbs-chip-locked",
            "NotAccessible" or "Not Accessible" => "lbs-chip lbs-chip-notaccessible",
            _ => "lbs-chip lbs-chip-unknown"
        };

    private static string FormatDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return DateTime.TryParse(value, out var date)
            ? date.ToString("dd MMM yyyy")
            : value;
    }
}