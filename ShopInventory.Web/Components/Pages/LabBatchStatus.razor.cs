using System.Globalization;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor;
using ShopInventory.Web.Features.Batches.Commands.UpdateBatchStatus;
using ShopInventory.Web.Features.Batches.Queries.SearchBatches;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Components.Pages;

public partial class LabBatchStatus : ComponentBase, IDisposable
{
    /// <summary>
    /// The status values SAP lets us set. The search API hands statuses back as
    /// labels, so the option value is the label — "Not Accessible" is normalized
    /// on the way into the update command.
    /// </summary>
    private static readonly (string Value, string Label)[] StatusOptions =
    [
        ("Released", "Released"),
        ("Locked", "Locked"),
        ("Not Accessible", "Not Accessible")
    ];

    /// <summary>The server caps a batch search at 100 in-stock rows.</summary>
    private const int ResultLimit = 100;

    private const int PageSize = 15;

    /// <summary>How long a committed row keeps its "Updated" confirmation.</summary>
    private static readonly TimeSpan UpdatedFlagDuration = TimeSpan.FromSeconds(2.8);

    [Inject]
    private IMediator Mediator { get; set; } = null!;

    [Inject]
    private ISnackbar Snackbar { get; set; } = null!;

    [Inject]
    private IJSRuntime JS { get; set; } = null!;

    private readonly HashSet<int> updatingBatchIds = new();
    private readonly HashSet<int> recentlyUpdated = new();
    private readonly Dictionary<int, CancellationTokenSource> updatedFlagTimers = new();
    private readonly List<BatchSearchItem> results = new();
    private string searchTerm = string.Empty;
    private string lastSearchTerm = string.Empty;
    private bool hasSearched;
    private bool isSearching;
    private bool isApplyingAll;
    private bool isDisposed;
    private int pageNumber = 1;

    // ── Derived view state ──────────────────────────────────────────────────

    private int PageCount => Math.Max(1, (int)Math.Ceiling(results.Count / (double)PageSize));

    private IEnumerable<BatchSearchItem> PageItems
        => results.Skip((pageNumber - 1) * PageSize).Take(PageSize);

    private string CountLabel
        => $"{results.Count} row{(results.Count == 1 ? "" : "s")} for {(string.IsNullOrWhiteSpace(lastSearchTerm) ? "all batches" : lastSearchTerm)}";

    private string RangeLabel
    {
        get
        {
            if (results.Count == 0)
            {
                return "No rows";
            }

            var start = ((pageNumber - 1) * PageSize) + 1;
            var end = Math.Min(pageNumber * PageSize, results.Count);
            return $"{start}–{end} of {results.Count} rows";
        }
    }

    private string PageLabel => $"Page {pageNumber} / {PageCount}";

    private List<BatchSearchItem> PendingItems
        => results.Where(IsDirty).ToList();

    private int PendingCount => results.Count(IsDirty);

    private static bool IsDirty(BatchSearchItem item)
        => !string.IsNullOrWhiteSpace(item.PendingStatus)
           && !string.Equals(item.PendingStatus, item.Status, StringComparison.Ordinal);

    private bool IsUpdating(int batchEntryId)
        => updatingBatchIds.Contains(batchEntryId);

    private static bool IsSelectableStatus(string? status)
        => StatusOptions.Any(option => string.Equals(option.Value, status, StringComparison.Ordinal));

    /// <summary>
    /// The three settable statuses, plus — where the batch currently carries one
    /// SAP will not let us set back, typically Unknown — that status as a
    /// disabled first row. Without it the control would show a blank trigger for
    /// a batch that does have a status, which reads as "no status" rather than
    /// "a status you cannot choose".
    /// </summary>
    /// <remarks>
    /// The families are the ones GetStatusToneClass gives the same status in the
    /// row beside this control.
    /// </remarks>
    private static IEnumerable<NocturneSelectOption<string>> StatusSelectOptionsFor(BatchSearchItem item)
    {
        var settable = StatusOptions.Select(option =>
            new NocturneSelectOption<string>(option.Value, option.Label, StatusFamily(option.Value)));

        return IsSelectableStatus(item.Status)
            ? settable
            : settable.Prepend(new NocturneSelectOption<string>(
                item.Status ?? string.Empty, GetStatusLabel(item.Status), StatusFamily(item.Status))
            {
                Disabled = true,
                RuleAfter = true
            });
    }

    private static string StatusFamily(string? status)
        => status switch
        {
            "Released" => "good",
            "Locked" => "warn",
            "NotAccessible" or "Not Accessible" => "bad",
            _ => "neutral"
        };

    // ── Search ──────────────────────────────────────────────────────────────

    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(searchTerm) || searchTerm.Trim().Length < 2)
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
            pageNumber = 1;
            ClearUpdatedFlags();
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

    // ── Paging ──────────────────────────────────────────────────────────────

    private void PreviousPage()
        => pageNumber = Math.Max(1, pageNumber - 1);

    private void NextPage()
        => pageNumber = Math.Min(PageCount, pageNumber + 1);

    // ── Committing changes ──────────────────────────────────────────────────

    private async Task UpdateStatusAsync(BatchSearchItem item)
    {
        if (!IsDirty(item) || IsUpdating(item.BatchEntryId))
        {
            return;
        }

        updatingBatchIds.Add(item.BatchEntryId);

        try
        {
            await ApplyOneAsync(item);
        }
        finally
        {
            updatingBatchIds.Remove(item.BatchEntryId);
        }
    }

    /// <summary>
    /// Commits every row holding an unsaved change. Rows are sent one at a time —
    /// each is its own SAP PATCH and its own audit entry — so a row that SAP
    /// rejects is reverted on its own and the rest still land.
    /// </summary>
    private async Task ApplyAllAsync()
    {
        var pending = PendingItems;
        if (pending.Count == 0 || isApplyingAll)
        {
            return;
        }

        var confirmed = await JS.InvokeAsync<bool>(
            "confirm",
            $"Apply {pending.Count} batch status change{(pending.Count == 1 ? "" : "s")} in SAP?");

        if (!confirmed)
        {
            return;
        }

        isApplyingAll = true;

        var applied = 0;
        var failed = 0;

        try
        {
            foreach (var item in pending)
            {
                updatingBatchIds.Add(item.BatchEntryId);
                StateHasChanged();

                try
                {
                    if (await ApplyOneAsync(item, announce: false))
                    {
                        applied++;
                    }
                    else
                    {
                        failed++;
                    }
                }
                finally
                {
                    updatingBatchIds.Remove(item.BatchEntryId);
                }
            }
        }
        finally
        {
            isApplyingAll = false;
        }

        if (applied > 0)
        {
            Snackbar.Add(
                $"{applied} batch status change{(applied == 1 ? "" : "s")} applied.",
                Severity.Success);
        }

        if (failed > 0)
        {
            Snackbar.Add(
                $"{failed} change{(failed == 1 ? "" : "s")} could not be applied and were reverted.",
                Severity.Error);
        }
    }

    /// <summary>
    /// Sends one row's pending status to SAP. On failure the row's pending status
    /// is put back so the table keeps telling the truth about what SAP holds.
    /// </summary>
    private async Task<bool> ApplyOneAsync(BatchSearchItem item, bool announce = true)
    {
        var target = item.PendingStatus!;

        var result = await Mediator.Send(new UpdateBatchStatusCommand(
            item.BatchEntryId,
            item.Status ?? string.Empty,
            target,
            item.BatchNumber,
            item.ItemCode,
            item.WarehouseCode));

        if (result.IsError)
        {
            item.PendingStatus = item.Status;

            if (announce)
            {
                Snackbar.Add(result.FirstError.Description, Severity.Error);
            }

            return false;
        }

        item.Status = target;
        FlagUpdated(item.BatchEntryId);

        if (announce)
        {
            Snackbar.Add($"Batch {item.BatchNumber} updated to {GetStatusLabel(target)}.", Severity.Success);
        }

        return true;
    }

    // ── The transient "Updated" confirmation on a committed row ─────────────

    private void FlagUpdated(int batchEntryId)
    {
        if (updatedFlagTimers.Remove(batchEntryId, out var running))
        {
            running.Cancel();
            running.Dispose();
        }

        recentlyUpdated.Add(batchEntryId);

        var cts = new CancellationTokenSource();
        updatedFlagTimers[batchEntryId] = cts;
        _ = ExpireUpdatedFlagAsync(batchEntryId, cts);
    }

    private async Task ExpireUpdatedFlagAsync(int batchEntryId, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(UpdatedFlagDuration, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        recentlyUpdated.Remove(batchEntryId);

        if (updatedFlagTimers.Remove(batchEntryId, out var finished))
        {
            finished.Dispose();
        }

        if (isDisposed)
        {
            return;
        }

        try
        {
            await InvokeAsync(StateHasChanged);
        }
        catch (ObjectDisposedException)
        {
            // The circuit went away while the flag was counting down.
        }
    }

    private void ClearUpdatedFlags()
    {
        foreach (var cts in updatedFlagTimers.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }

        updatedFlagTimers.Clear();
        recentlyUpdated.Clear();
    }

    public void Dispose()
    {
        isDisposed = true;
        ClearUpdatedFlags();
    }

    // ── Formatting ──────────────────────────────────────────────────────────

    private static string GetStatusLabel(string? status)
        => status switch
        {
            "NotAccessible" => "Not Accessible",
            null or "" => "Unknown",
            _ => status
        };

    /// <summary>
    /// Status is drawn as a coloured dot plus the word. Released is the only one
    /// that wears the accent; the rest step down the neutral ramp as they get
    /// more restrictive.
    /// </summary>
    private static string GetStatusToneClass(string? status)
        => status switch
        {
            "Released" => "is-released",
            "Locked" => "is-locked",
            "NotAccessible" or "Not Accessible" => "is-blocked",
            _ => "is-unknown"
        };

    private static string FormatQuantity(decimal quantity)
        => quantity.ToString("N1", CultureInfo.InvariantCulture);

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
