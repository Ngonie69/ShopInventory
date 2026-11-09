using System.Globalization;
using MediatR;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using ShopInventory.Web.Features.SapTransferApprovals.Commands.AddApprovedSapTransfer;
using ShopInventory.Web.Features.SapTransferApprovals.Queries.GetSapTransferApproval;
using ShopInventory.Web.Features.SapTransferApprovals.Queries.GetSapTransferApprovals;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

/// <summary>
/// The inventory transfers keyed in the SAP client that SAP's approval procedure has approved and nobody
/// has added, and the one thing done with one here: add it, which posts the transfer in SAP.
/// </summary>
/// <remarks>
/// Everything is read live from SAP through the API. The search and every filter are sent to SAP, which
/// filters and counts, and a depot controller's queue is already narrowed there to the transfers leaving
/// its own warehouses. Deciding stays in the B1 client: a draft only appears here once SAP has approved it.
/// </remarks>
public partial class SapTransferApprovals : IDisposable
{
    private const int PageSize = 25;

    /// <summary>How long the typing rests before the search is sent to SAP.</summary>
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(400);

    /// <summary>The "Raised" choices. Each becomes a creation-day bound SAP applies.</summary>
    private static readonly (string Key, string Label)[] AgeChoices =
    [
        ("any", "Any time"),
        ("today", "Today"),
        ("week", "Last 7 days"),
        ("over7", "7+ days waiting"),
        ("over30", "30+ days waiting")
    ];

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private IMasterDataCacheService CacheService { get; set; } = default!;
    [Inject] private ILogger<SapTransferApprovals> Logger { get; set; } = default!;

    private readonly CancellationTokenSource disposal = new();

    private List<SapTransferApprovalListItemDto> drafts = [];
    private List<string>? scopedWarehouses;
    private int totalCount;
    private int currentPage = 1;

    /// <summary>
    /// The cursor each reached page was read with; page 1 is always the top. The queue is newest-first
    /// and live — a transfer approved behind somebody lands at the top, and every add takes a row out —
    /// so a page keyed to where the last one stopped does not shift under them. A page with no cursor
    /// here (a jump straight to the last page) is read by offset, and its own next page then has one.
    /// </summary>
    private readonly Dictionary<int, int?> pageCursors = new() { [1] = null };

    private int? nextCursor;
    private bool isLoading = true;
    private string? errorMessage;

    private string? searchText;
    private string? appliedSearch;
    private int searchVersion;

    private bool filtersOpen;
    private readonly List<string> fromFilter = [];
    private readonly List<string> toFilter = [];
    private string ageFilter = "any";
    private bool attachedOnly;

    private List<NocturnePickerOption> allWarehouseOptions = [];
    private bool isLoadingWarehouses = true;

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

    private string RangeText
    {
        get
        {
            var first = ((currentPage - 1) * PageSize) + 1;
            var last = Math.Min(currentPage * PageSize, totalCount);
            return $"{first:N0}–{last:N0}";
        }
    }

    /// <summary>Whether the search or a filter is narrowing the queue, so the count is of matches.</summary>
    private bool IsNarrowed => ActiveFilters.Count > 0 || !string.IsNullOrWhiteSpace(appliedSearch);

    private string EmptyMessage => IsNarrowed
        ? "No approved transfer matches this search and these filters"
        : "SAP holds no approved transfers waiting to be added";

    private string FilterSummary
    {
        get
        {
            if (ActiveFilters.Count == 0)
            {
                return "No filters set. Choices in one group widen; groups narrow each other.";
            }

            if (isLoading)
            {
                return "Reading SAP…";
            }

            var matches = totalCount == 1 ? "1 transfer matches" : $"{totalCount:N0} transfers match";
            return $"{matches} {(ActiveFilters.Count == 1 ? "this filter" : $"these {ActiveFilters.Count} filters")}.";
        }
    }

    /// <summary>
    /// Where a transfer can leave from. A depot controller's queue only holds its own warehouses, so
    /// offering any other would only ever find nothing.
    /// </summary>
    private List<NocturnePickerOption> FromOptions => WithoutChosen(
        scopedWarehouses is { Count: > 0 }
            ? allWarehouseOptions.Where(option => scopedWarehouses.Contains(option.Value, StringComparer.OrdinalIgnoreCase)).ToList()
            : allWarehouseOptions,
        fromFilter);

    private List<NocturnePickerOption> ToOptions => WithoutChosen(allWarehouseOptions, toFilter);

    /// <summary>Every filter in force, as a chip that removes it.</summary>
    private List<(string Label, EventCallback Remove)> ActiveFilters
    {
        get
        {
            var active = new List<(string, EventCallback)>();
            foreach (var code in fromFilter.ToList())
            {
                active.Add(($"From {code}", EventCallback.Factory.Create(this, () => RemoveWarehouseAsync(fromFilter, code))));
            }

            foreach (var code in toFilter.ToList())
            {
                active.Add(($"To {code}", EventCallback.Factory.Create(this, () => RemoveWarehouseAsync(toFilter, code))));
            }

            if (ageFilter != "any")
            {
                var label = AgeChoices.First(choice => choice.Key == ageFilter).Label;
                active.Add(($"Raised: {label}", EventCallback.Factory.Create(this, () => SetAgeAsync("any"))));
            }

            if (attachedOnly)
            {
                active.Add(("Has attachment", EventCallback.Factory.Create(this, ToggleAttachedAsync)));
            }

            return active;
        }
    }

    /// <summary>
    /// The page's rows by the day each was raised, in the order the days first appear — the queue is
    /// newest first, so that is newest day first.
    /// </summary>
    private IEnumerable<IGrouping<DateTime?, SapTransferApprovalListItemDto>> DayGroups =>
        drafts.GroupBy(draft => RaisedDate(draft)?.Date);

    /// <summary>The page numbers to offer — first, last, and either side of this one — null for a gap.</summary>
    private List<int?> PageButtons
    {
        get
        {
            var wanted = new SortedSet<int> { 1, TotalPages, currentPage };
            if (currentPage > 1)
            {
                wanted.Add(currentPage - 1);
            }

            if (currentPage < TotalPages)
            {
                wanted.Add(currentPage + 1);
            }

            var buttons = new List<int?>();
            int? previous = null;
            foreach (var number in wanted)
            {
                if (previous is int before && number - before > 1)
                {
                    buttons.Add(null);
                }

                buttons.Add(number);
                previous = number;
            }

            return buttons;
        }
    }

    /// <summary>The transfer requests the open draft was copied from that the add would close.</summary>
    private List<int> OpenLinkedRequests => detail?.LinkedRequests
        .Where(linked => linked.Status == "Open")
        .Select(linked => linked.DocNum ?? linked.DocEntry)
        .ToList() ?? [];

    protected override async Task OnInitializedAsync()
    {
        await Task.WhenAll(LoadAsync(), LoadWarehousesAsync());
    }

    private async Task LoadWarehousesAsync()
    {
        try
        {
            var warehouses = await CacheService.GetWarehousesAsync(cancellationToken: disposal.Token);
            allWarehouseOptions = WarehouseOptions.ByCode(warehouses.Where(warehouse => warehouse.IsActive));
        }
        catch (OperationCanceledException) when (disposal.IsCancellationRequested)
        {
            // The page is gone.
        }
        catch (Exception ex)
        {
            // The pickers read empty; the search box still filters by warehouse.
            Logger.LogWarning(ex, "Could not load the warehouses for the SAP-held transfer filters");
        }
        finally
        {
            isLoadingWarehouses = false;
        }
    }

    private async Task LoadAsync()
    {
        isLoading = true;
        StateHasChanged();

        try
        {
            var cursor = pageCursors.GetValueOrDefault(currentPage);
            var result = await Mediator.Send(
                new GetSapTransferApprovalsQuery(currentPage, PageSize, cursor, BuildFilter()), disposal.Token);
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
            if (nextCursor is int below)
            {
                pageCursors[currentPage + 1] = below;
            }
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
    /// The search and filters as SAP will apply them. A whole number in the search box is a draft number;
    /// anything else is a warehouse the transfer leaves or enters.
    /// </summary>
    private SapTransferQueueFilter BuildFilter()
    {
        var text = string.IsNullOrWhiteSpace(appliedSearch) ? null : appliedSearch.Trim();
        int? draftNum = text is not null && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number
            : null;

        var today = DateTime.Today;
        var (createdFrom, createdTo) = ageFilter switch
        {
            "today" => ((DateTime?)today, (DateTime?)null),
            "week" => (today.AddDays(-6), null),
            "over7" => (null, today.AddDays(-7)),
            "over30" => (null, today.AddDays(-30)),
            _ => ((DateTime?)null, (DateTime?)null)
        };

        return new SapTransferQueueFilter
        {
            Warehouse = draftNum is null ? text : null,
            DraftNum = draftNum,
            FromWarehouses = fromFilter.ToList(),
            ToWarehouses = toFilter.ToList(),
            CreatedFrom = createdFrom,
            CreatedTo = createdTo,
            AttachedOnly = attachedOnly
        };
    }

    /// <summary>
    /// Keeps the box in step with the typing and sends the search once it stops. The version counter is
    /// the debounce: a keystroke arriving while this one waits bumps it, and the older call drops out.
    /// </summary>
    private async Task OnSearchInputAsync(ChangeEventArgs args)
    {
        searchText = args.Value?.ToString();
        var version = ++searchVersion;

        await Task.Delay(SearchDebounce);
        if (searchVersion != version)
        {
            return;
        }

        await ApplySearchAsync();
    }

    private async Task ClearSearchAsync()
    {
        searchText = null;
        searchVersion++;
        await ApplySearchAsync();
    }

    private async Task ApplySearchAsync()
    {
        var next = string.IsNullOrWhiteSpace(searchText) ? null : searchText.Trim();
        if (string.Equals(next, appliedSearch, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        appliedSearch = next;
        await ReloadFromTopAsync();
    }

    private void ToggleFilters() => filtersOpen = !filtersOpen;

    private async Task AddWarehouseAsync(List<string> into, string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || into.Contains(code, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        into.Add(code);
        await ReloadFromTopAsync();
    }

    private async Task RemoveWarehouseAsync(List<string> from, string code)
    {
        if (from.RemoveAll(existing => string.Equals(existing, code, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            await ReloadFromTopAsync();
        }
    }

    private async Task SetAgeAsync(string key)
    {
        if (ageFilter == key)
        {
            return;
        }

        ageFilter = key;
        await ReloadFromTopAsync();
    }

    private async Task ToggleAttachedAsync()
    {
        attachedOnly = !attachedOnly;
        await ReloadFromTopAsync();
    }

    private async Task ClearFiltersAsync()
    {
        if (ActiveFilters.Count == 0)
        {
            return;
        }

        fromFilter.Clear();
        toFilter.Clear();
        ageFilter = "any";
        attachedOnly = false;
        await ReloadFromTopAsync();
    }

    private async Task ClearEverythingAsync()
    {
        searchText = null;
        appliedSearch = null;
        searchVersion++;
        fromFilter.Clear();
        toFilter.Clear();
        ageFilter = "any";
        attachedOnly = false;
        await ReloadFromTopAsync();
    }

    /// <summary>Back to the top, for a change of which queue is being read.</summary>
    private Task ReloadFromTopAsync()
    {
        currentPage = 1;
        pageCursors.Clear();
        pageCursors[1] = null;
        nextCursor = null;
        return LoadAsync();
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

        currentPage++;
        await LoadAsync();
    }

    private async Task GoToPageAsync(int page)
    {
        if (page < 1 || page > TotalPages || page == currentPage)
        {
            return;
        }

        currentPage = page;
        await LoadAsync();
    }

    private async Task OpenAsync(int draftEntry)
    {
        detailEntry = draftEntry;
        detail = null;
        detailError = null;
        showAddConfirm = false;
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
            // SAP has answered either way. Left open, the confirm would offer the same Add again once the
            // snackbar had faded.
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

    private static List<NocturnePickerOption> WithoutChosen(List<NocturnePickerOption> options, List<string> chosen) =>
        chosen.Count == 0
            ? options
            : options.Where(option => !chosen.Contains(option.Value, StringComparer.OrdinalIgnoreCase)).ToList();

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

    /// <summary>
    /// When the transfer was raised: the draft's creation day — the field the "Raised" filter is applied
    /// to in SAP, so a row never sits under a day the filter disagrees with — else the request's, else
    /// the document date.
    /// </summary>
    private static DateTime? RaisedDate(SapTransferApprovalListItemDto draft) =>
        draft.CreatedDate ?? draft.RequestedDate ?? draft.DocDate;

    private static int? DaysAgo(DateTime? day) => day is null ? null : (DateTime.Today - day.Value.Date).Days;

    private static string AgeText(DateTime? raised) => DaysAgo(raised) switch
    {
        null => string.Empty,
        <= 0 => "today",
        1 => "yesterday",
        var days => $"{days} days ago"
    };

    private static string DayRelative(DateTime? day) => DaysAgo(day) switch
    {
        null => "Date not recorded",
        <= 0 => "Today",
        1 => "Yesterday",
        var days => $"{days} days ago"
    };

    private static string DayLabel(DateTime? day) => day is null
        ? string.Empty
        : day.Value.ToString(day.Value.Year == DateTime.Today.Year ? "dddd d MMMM" : "dddd d MMMM yyyy", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        disposal.Cancel();
        disposal.Dispose();
    }
}
