using MediatR;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using ShopInventory.Web.Features.SapTransferApprovals.Commands.AddApprovedSapTransfer;
using ShopInventory.Web.Features.SapTransferApprovals.Queries.GetSapTransferApproval;
using ShopInventory.Web.Features.SapTransferApprovals.Queries.GetSapTransferApprovals;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Components.Pages;

/// <summary>
/// The inventory transfers keyed in the SAP client that SAP's approval procedure has approved and nobody
/// has added, and the one thing done with one here: add it, which posts the transfer in SAP.
/// </summary>
/// <remarks>
/// Everything is read live from SAP through the API. The warehouse filter is sent to SAP, which filters
/// and counts, and a depot controller's queue is already narrowed there to the transfers leaving its own
/// warehouses. Deciding stays in the B1 client: a draft only appears here once SAP has approved it.
/// </remarks>
public partial class SapTransferApprovals : IDisposable
{
    private const int PageSize = 25;

    /// <summary>How long the typing rests before the warehouse filter is sent to SAP.</summary>
    private static readonly TimeSpan FilterDebounce = TimeSpan.FromMilliseconds(400);

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private ILogger<SapTransferApprovals> Logger { get; set; } = default!;

    private readonly CancellationTokenSource disposal = new();

    private List<SapTransferApprovalListItemDto> drafts = [];
    private List<string>? scopedWarehouses;
    private int totalCount;
    private int currentPage = 1;

    /// <summary>
    /// The cursor each page was read with, one per page reached, <c>[0]</c> being null for the top. The
    /// queue is newest-first and live — a transfer approved behind somebody lands at the top, and every
    /// add takes a row out — so a page keyed to where the last one stopped does not shift under them.
    /// </summary>
    private readonly List<int?> pageCursors = [null];

    private int? nextCursor;
    private bool isLoading = true;
    private string? errorMessage;

    private string? warehouseFilter;
    private string? appliedWarehouseFilter;
    private int warehouseFilterVersion;

    private int? detailEntry;
    private SapTransferApprovalDetailDto? detail;
    private bool isLoadingDetail;
    private string? detailError;

    private bool isSubmitting;
    private bool showAddConfirm;

    /// <summary>What the last add said, kept once the drawer closes: the draft has left the queue.</summary>
    private string? addedNotice;
    private string? addedNoticeSource;
    private bool addedNoticeNeedsAttention;

    private int TotalPages => Math.Max(1, (int)Math.Ceiling(totalCount / (double)PageSize));

    /// <summary>SAP's answer decides: a short page means there is nothing below it.</summary>
    private bool CanGoNext => nextCursor is not null && currentPage < TotalPages;

    private string RangeLabel
    {
        get
        {
            if (totalCount == 0)
            {
                return "No transfers";
            }

            var first = ((currentPage - 1) * PageSize) + 1;
            var last = Math.Min(currentPage * PageSize, totalCount);
            return $"{first}–{last} of {totalCount:N0}";
        }
    }

    private string EmptyMessage => string.IsNullOrWhiteSpace(appliedWarehouseFilter)
        ? "SAP holds no approved transfers waiting to be added"
        : $"No approved transfers leave or enter {appliedWarehouseFilter}";

    /// <summary>The transfer requests the open draft was copied from that the add would close.</summary>
    private List<int> OpenLinkedRequests => detail?.LinkedRequests
        .Where(linked => linked.Status == "Open")
        .Select(linked => linked.DocNum ?? linked.DocEntry)
        .ToList() ?? [];

    protected override Task OnInitializedAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        isLoading = true;
        StateHasChanged();

        try
        {
            var cursor = currentPage - 1 < pageCursors.Count ? pageCursors[currentPage - 1] : null;
            var result = await Mediator.Send(
                new GetSapTransferApprovalsQuery(currentPage, PageSize, cursor, appliedWarehouseFilter), disposal.Token);
            if (result.IsError)
            {
                errorMessage = result.FirstError.Description;
                drafts = [];
                totalCount = 0;
                nextCursor = null;
                return;
            }

            errorMessage = null;
            drafts = result.Value.Items;
            totalCount = result.Value.TotalCount;
            nextCursor = result.Value.NextCursor;
            scopedWarehouses = result.Value.ScopedWarehouses;
        }
        catch (OperationCanceledException) when (disposal.IsCancellationRequested)
        {
            // The page is gone.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load the transfers SAP is holding");
            errorMessage = "The transfers SAP is holding could not be read.";
            drafts = [];
            totalCount = 0;
            nextCursor = null;
        }
        finally
        {
            isLoading = false;
            if (!disposal.IsCancellationRequested)
            {
                StateHasChanged();
            }
        }
    }

    /// <summary>
    /// Keeps the box in step with the typing and sends the filter once it stops. The version counter is
    /// the debounce: a keystroke arriving while this one waits bumps it, and the older call drops out.
    /// </summary>
    private async Task OnWarehouseFilterInputAsync(ChangeEventArgs args)
    {
        warehouseFilter = args.Value?.ToString();
        var version = ++warehouseFilterVersion;

        await Task.Delay(FilterDebounce);
        if (warehouseFilterVersion != version)
        {
            return;
        }

        await ApplyWarehouseFilterAsync();
    }

    private async Task ClearWarehouseFilterAsync()
    {
        warehouseFilter = null;
        warehouseFilterVersion++;
        await ApplyWarehouseFilterAsync();
    }

    private async Task ApplyWarehouseFilterAsync()
    {
        var next = string.IsNullOrWhiteSpace(warehouseFilter) ? null : warehouseFilter.Trim();
        if (string.Equals(next, appliedWarehouseFilter, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        appliedWarehouseFilter = next;
        RestartPaging();
        await LoadAsync();
    }

    /// <summary>Back to the top, for a change of which queue is being read.</summary>
    private void RestartPaging()
    {
        currentPage = 1;
        pageCursors.Clear();
        pageCursors.Add(null);
        nextCursor = null;
    }

    private Task RefreshAsync() => LoadAsync();

    private async Task PreviousPageAsync()
    {
        if (currentPage <= 1)
        {
            return;
        }

        currentPage--;
        await LoadAsync();
    }

    private async Task NextPageAsync()
    {
        if (!CanGoNext)
        {
            return;
        }

        if (pageCursors.Count == currentPage)
        {
            pageCursors.Add(nextCursor);
        }
        else
        {
            pageCursors[currentPage] = nextCursor;
        }

        currentPage++;
        await LoadAsync();
    }

    private async Task OpenAsync(int draftEntry)
    {
        detailEntry = draftEntry;
        detail = null;
        detailError = null;
        isLoadingDetail = true;
        StateHasChanged();

        await LoadDetailAsync(draftEntry);
    }

    private async Task LoadDetailAsync(int draftEntry)
    {
        try
        {
            var result = await Mediator.Send(new GetSapTransferApprovalQuery(draftEntry), disposal.Token);

            // The drawer may have been closed, or opened on another row, while this was in flight.
            if (detailEntry != draftEntry)
            {
                return;
            }

            if (result.IsError)
            {
                detail = null;
                detailError = result.FirstError.Description;
                return;
            }

            detail = result.Value;
            detailError = null;
        }
        catch (OperationCanceledException) when (disposal.IsCancellationRequested)
        {
            // The page is gone.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load SAP transfer draft {DraftEntry}", draftEntry);
            if (detailEntry == draftEntry)
            {
                detail = null;
                detailError = "This draft could not be read from SAP.";
            }
        }
        finally
        {
            if (detailEntry == draftEntry && !disposal.IsCancellationRequested)
            {
                isLoadingDetail = false;
                StateHasChanged();
            }
        }
    }

    private void CloseDetail()
    {
        detailEntry = null;
        detail = null;
        detailError = null;
        isLoadingDetail = false;
        showAddConfirm = false;
    }

    private void OpenAddConfirm() => showAddConfirm = true;

    private void CloseAddConfirm() => showAddConfirm = false;

    private async Task AddAsync()
    {
        if (detail is null || detailEntry is not int draftEntry || isSubmitting)
        {
            return;
        }

        isSubmitting = true;
        StateHasChanged();

        string? refusal = null;
        AddApprovedSapTransferResultDto? added = null;
        var route = $"{detail.Draft.FromWarehouse} → {detail.Draft.ToWarehouse}";
        var draftNum = detail.Draft.DraftNum;

        try
        {
            // A new key per click; the API claims the add per draft whatever key is sent, so a repeat of
            // an add that already landed replays its answer instead of posting a second transfer.
            // Deliberately not the page's disposal token: once sent, the add must be allowed to finish.
            var result = await Mediator.Send(new AddApprovedSapTransferCommand(draftEntry, Guid.NewGuid().ToString("N")));

            if (result.IsError)
            {
                refusal = result.FirstError.Description;
                detailError = refusal;
                Snackbar.Add(refusal, Severity.Error);
            }
            else
            {
                added = result.Value;
                detailError = null;
                Snackbar.Add(added.Message, added.RequestsLeftOpen.Count > 0 || !added.Resolved ? Severity.Warning : Severity.Success);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to add SAP transfer draft {DraftEntry}", draftEntry);
            refusal = "The transfer could not be added in SAP.";
            detailError = refusal;
            Snackbar.Add(refusal, Severity.Error);
        }
        finally
        {
            isSubmitting = false;
            // SAP has answered either way. Left open, the dialog covered the drawer and offered the same
            // Add again once the snackbar had faded.
            showAddConfirm = false;
        }

        if (added is not null)
        {
            // The draft is closed in SAP now and leaves the queue: keep what the add said on the page,
            // close the drawer and re-read only the list.
            addedNotice = added.Message;
            addedNoticeSource = $"From draft {draftNum} · {route}";
            addedNoticeNeedsAttention = added.RequestsLeftOpen.Count > 0 || !added.Resolved;
            CloseDetail();
            await LoadAsync();
            return;
        }

        await Task.WhenAll(LoadDetailAsync(draftEntry), LoadAsync());

        // The reload clears detailError when the draft reads back; the reason the add was refused is the
        // thing the person needs, so it is put back.
        if (refusal is not null && detailEntry == draftEntry && detail is not null)
        {
            detailError = refusal;
        }
    }

    private void DismissAddedNotice()
    {
        addedNotice = null;
        addedNoticeSource = null;
        addedNoticeNeedsAttention = false;
    }

    private string RowClass(SapTransferApprovalListItemDto draft)
    {
        var classes = new List<string>();
        if (detailEntry == draft.DraftEntry)
        {
            classes.Add("is-open-row");
        }

        if (!draft.CanAdd)
        {
            classes.Add("is-dim");
        }

        return string.Join(' ', classes);
    }

    private static string StatusModifier(string? status) => status?.ToLowerInvariant() switch
    {
        "pending" => "is-pending",
        "approved" => "is-approved",
        "notapproved" => "is-rejected",
        _ => string.Empty
    };

    /// <summary>A line can take its own warehouses; it is shown only when they are not the draft's.</summary>
    private bool LineRouteDiffers(SapTransferDraftLineDto line) =>
        detail is not null
        && (!string.Equals(line.FromWarehouse, detail.Draft.FromWarehouse, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(line.ToWarehouse, detail.Draft.ToWarehouse, StringComparison.OrdinalIgnoreCase));

    private static string? RowComments(SapTransferApprovalListItemDto draft) =>
        string.IsNullOrWhiteSpace(draft.Comments) ? draft.RequestRemarks : draft.Comments;

    private static string FormatDate(DateTime? value) => value?.ToString("dd MMM yyyy") ?? "—";

    /// <summary>When the transfer was raised: the approval request's date, else the draft's document date.</summary>
    private static DateTime? RaisedDate(SapTransferApprovalListItemDto draft) =>
        draft.RequestedDate ?? draft.CreatedDate ?? draft.DocDate;

    private static string AgeText(DateTime? raised)
    {
        if (raised is null)
        {
            return string.Empty;
        }

        var days = (DateTime.UtcNow.Date - raised.Value.Date).Days;
        return days switch
        {
            <= 0 => "today",
            1 => "yesterday",
            _ => $"{days} days ago"
        };
    }

    /// <summary>The draft and request numbers, folded under the route — what you quote once you are in SAP.</summary>
    private static string RowMeta(SapTransferApprovalListItemDto draft)
    {
        var parts = new List<string> { $"draft {draft.DraftNum}" };
        if (draft.RequestCode is int code)
        {
            parts.Add($"request {code}");
        }

        return string.Join("  ·  ", parts);
    }

    public void Dispose()
    {
        disposal.Cancel();
        disposal.Dispose();
    }
}
