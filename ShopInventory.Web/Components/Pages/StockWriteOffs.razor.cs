using System.Globalization;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using ShopInventory.Web.Features.StockWriteOffs.Commands.CreateStockWriteOff;
using ShopInventory.Web.Features.StockWriteOffs.Queries.GetStockWriteOff;
using ShopInventory.Web.Features.StockWriteOffs.Queries.GetStockWriteOffItems;
using ShopInventory.Web.Features.StockWriteOffs.Queries.GetStockWriteOffReasons;
using ShopInventory.Web.Features.StockWriteOffs.Queries.GetStockWriteOffs;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

/// <summary>
/// Writing stock off a warehouse.
/// </summary>
/// <remarks>
/// <para>
/// A line is one thing counted — an item, and the batch it came out of where SAP manages the item
/// that way — because that is how a store person counts and how SAP records it. Five of one batch and
/// three of another are two rows, not one row with two batches.
/// </para>
/// <para>
/// The batch picker is not a convenience: SAP refuses the whole goods issue when a batch-managed line
/// names no batch, so the page will not let such a line be added at all. The API refuses it a second
/// time, because a caller that is not this page still has to be told. The batches come back from SAP
/// with what each holds, so the page also refuses a line asking for more of a batch than SAP shows on
/// hand — counted across every line naming that batch, since SAP measures the document as a whole.
/// That is a courtesy, not the guard: stock moves between the count and the post, and the API reads
/// SAP again before it issues anything.
/// </para>
/// <para>
/// The item picker is filled once, from the Web's own PostgreSQL copy of the item master, and not
/// per warehouse from SAP: a code and a description are all it needs, and asking the Service Layer
/// for every item holding stock in a warehouse each time one was chosen was the dearest read on the
/// page. Whether the warehouse holds the item is settled by the batch read and by the API when it
/// posts — both of which are still SAP, and both of which are per item rather than per warehouse.
/// </para>
/// <para>
/// Nothing is posted from the count itself. The preview beside it says what the goods issue will
/// be, and posting happens only from a review dialog that restates it, because what leaves SAP here
/// cannot be taken back from this page.
/// </para>
/// </remarks>
public partial class StockWriteOffs
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IMasterDataCacheService MasterData { get; set; } = default!;
    [Inject] private IProductService ProductService { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private ILogger<StockWriteOffs> Logger { get; set; } = default!;

    private const int PageSize = 30;

    /// <summary>How close an expiry has to be before a batch is flagged as the likely one.</summary>
    private const int ExpirySoonDays = 7;

    private readonly List<StockWriteOffSummary> writeOffs = [];
    private readonly List<DraftLine> draft = [];
    private Dictionary<string, int> statusCounts = [];
    private int totalCount;
    private int loadedPage;

    private StockWriteOffDetail? selected;
    private string? errorMessage;
    private string statusFilter = string.Empty;
    private string? warehouseFilter;

    private bool hasInitialized;
    private bool isLoadingList = true;
    private bool isLoadingMore;
    private bool isLoadingItems = true;
    private bool isLoadingBatches;
    private bool isPosting;
    private bool isReviewing;
    private bool focusReview;
    private ElementReference reviewDialog;

    // The write-off being built.
    private string? warehouseCode;
    private string? reason;
    private DateTime? docDate;
    private string? remarks;
    private bool? reasonsRecordedInSap;

    // The line being built.
    private string? draftItemCode;
    private string? draftItemName;
    private string? draftBatchNumber;
    private decimal draftQuantity;
    private bool draftNeedsBatch;

    private List<WarehouseDto> warehouses = [];
    private IReadOnlyList<StockWriteOffItem> items = [];
    private DateTime? catalogueSyncedAt;
    private List<BatchView> batchViews = [];
    private List<StockWriteOffReason> reasons = [];

    /// <summary>
    /// The one place a client request id is made, and it is made per attempt rather than per line
    /// edit: it is what lets a retry after a lost reply find the write-off it already raised, and
    /// what stops a double-click raising two.
    /// </summary>
    private string? attemptId;

    private static readonly (string Value, string Label)[] StatusTabs =
    [
        (string.Empty, "All"),
        ("Pending", "To post"),
        ("Posting", "Posting"),
        ("Posted", "Written off"),
        ("PostFailed", "Failed")
    ];

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (focusReview)
        {
            focusReview = false;
            await reviewDialog.FocusAsync();
        }

        if (!firstRender || hasInitialized)
        {
            return;
        }

        hasInitialized = true;

        await Task.WhenAll(LoadListAsync(), LoadWarehousesAsync(), LoadReasonsAsync(), LoadItemsAsync());
        StateHasChanged();
    }

    // ── Options for the Nocturne controls ───────────────────────────────────────────────────────

    private IEnumerable<INocturneSelectOption<string>> WarehouseOptions =>
        warehouses
            .Where(warehouse => !string.IsNullOrWhiteSpace(warehouse.WarehouseCode))
            .Select(warehouse => new NocturneSelectOption<string>(
                warehouse.WarehouseCode!,
                warehouse.WarehouseName ?? warehouse.WarehouseCode!)
            {
                Hint = warehouse.WarehouseCode
            });

    private IEnumerable<INocturneSelectOption<string>> WarehouseFilterOptions =>
        WarehouseOptions.Prepend(NocturneSelectOption.All("All warehouses"));

    private IReadOnlyList<NocturnePickerOption> ItemOptions =>
        items
            .Select(item => new NocturnePickerOption(
                item.ItemCode,
                item.ItemName ?? item.ItemCode,
                item.ItemCode))
            .ToList();

    // ── Loading ────────────────────────────────────────────────────────────────────────────────

    private async Task LoadListAsync()
    {
        isLoadingList = true;
        try
        {
            writeOffs.Clear();
            loadedPage = 0;
            await LoadPageAsync(1);
        }
        finally
        {
            isLoadingList = false;
        }
    }

    private async Task LoadOlderAsync()
    {
        isLoadingMore = true;
        try
        {
            await LoadPageAsync(loadedPage + 1);
        }
        finally
        {
            isLoadingMore = false;
        }
    }

    private async Task LoadPageAsync(int page)
    {
        var result = await Mediator.Send(new GetStockWriteOffsQuery(
            string.IsNullOrEmpty(statusFilter) ? null : statusFilter,
            string.IsNullOrEmpty(warehouseFilter) ? null : warehouseFilter,
            page,
            PageSize));

        if (result.IsError)
        {
            errorMessage = result.FirstError.Description;
            return;
        }

        // A write-off raised between two pages shifts the next page by one, so an id already shown
        // is skipped rather than listed twice.
        var shown = writeOffs.Select(row => row.Id).ToHashSet();
        writeOffs.AddRange(result.Value.Items.Where(row => shown.Add(row.Id)));
        statusCounts = result.Value.StatusCounts;
        totalCount = result.Value.TotalCount;
        loadedPage = page;
    }

    private async Task LoadWarehousesAsync()
    {
        try
        {
            warehouses = await MasterData.GetWarehousesAsync();
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Could not load the warehouses for the write-off page");
            warehouses = [];
        }
    }

    private async Task LoadReasonsAsync()
    {
        var result = await Mediator.Send(new GetStockWriteOffReasonsQuery());
        if (result.IsError)
        {
            Logger.LogWarning("Could not load the write-off reasons: {Message}", result.FirstError.Description);
            return;
        }

        reasons = result.Value.Reasons;
        reasonsRecordedInSap = result.Value.RecordedInSap;
    }

    /// <summary>
    /// The catalogue, read once per visit from the Web's PostgreSQL copy of the item master. It is
    /// not reread when the warehouse changes, because it does not depend on the warehouse.
    /// </summary>
    private async Task LoadItemsAsync()
    {
        isLoadingItems = true;
        try
        {
            var result = await Mediator.Send(new GetStockWriteOffItemsQuery());
            if (result.IsError)
            {
                errorMessage = result.FirstError.Description;
                return;
            }

            items = result.Value.Items;
            catalogueSyncedAt = result.Value.SyncedAt;
        }
        finally
        {
            isLoadingItems = false;
        }
    }

    private Task OnWarehouseChangedAsync(string? code)
    {
        if (string.Equals(code, warehouseCode, StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        warehouseCode = code;

        // The lines already counted belong to the warehouse they were counted at, so changing it
        // clears them rather than quietly moving them somewhere they were never counted. The item
        // list itself stays: it is the whole catalogue, not the warehouse's stock.
        draft.Clear();
        ClearDraftLine();
        return Task.CompletedTask;
    }

    private async Task OnDraftItemChangedAsync(string? itemCode)
    {
        draftItemCode = itemCode;
        draftBatchNumber = null;
        batchViews = [];
        draftNeedsBatch = false;

        if (string.IsNullOrWhiteSpace(itemCode) || string.IsNullOrWhiteSpace(warehouseCode))
        {
            draftItemName = null;
            return;
        }

        var item = items.FirstOrDefault(row =>
            string.Equals(row.ItemCode, itemCode, StringComparison.OrdinalIgnoreCase));
        draftItemName = item?.ItemName;
        draftNeedsBatch = item?.ManagesBatches == true;

        if (!draftNeedsBatch)
        {
            return;
        }

        isLoadingBatches = true;
        try
        {
            var response = await ProductService.GetProductBatchesAsync(itemCode, warehouseCode);
            batchViews = (response?.Batches ?? [])
                .Where(batch => batch.Quantity > 0 && !string.IsNullOrWhiteSpace(batch.BatchNumber))
                .Select(BatchView.From)
                // Soonest expiry first, because the batch nearest its date is the likeliest to be the
                // one being written off. A batch with no expiry recorded goes last.
                .OrderBy(batch => batch.ExpiresOn ?? DateTime.MaxValue)
                .ThenBy(batch => batch.Number, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (batchViews.Count == 1)
            {
                draftBatchNumber = batchViews[0].Number;
            }
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Could not load batches for {ItemCode} in {Warehouse}", itemCode, warehouseCode);
            errorMessage = $"The batches of {itemCode} in {warehouseCode} could not be read.";
        }
        finally
        {
            isLoadingBatches = false;
        }
    }

    // ── The count ──────────────────────────────────────────────────────────────────────────────

    private BatchView? DraftBatch =>
        draftNeedsBatch && draftBatchNumber is not null
            ? batchViews.FirstOrDefault(batch => string.Equals(batch.Number, draftBatchNumber, StringComparison.OrdinalIgnoreCase))
            : null;

    /// <summary>What the lines already counted take out of the chosen batch.</summary>
    private decimal DraftedFromBatch =>
        DraftBatch is { } batch
            ? draft.Where(line => SameLine(line, draftItemCode, batch.Number)).Sum(line => line.Quantity)
            : 0;

    private bool DraftOverStock =>
        DraftBatch is { } batch && draftQuantity > 0 && draftQuantity + DraftedFromBatch > batch.OnHand;

    private string QuantityHint
    {
        get
        {
            if (string.IsNullOrWhiteSpace(draftItemCode))
            {
                return "Choose an item first";
            }

            if (!draftNeedsBatch)
            {
                return "SAP checks the quantity on hand when it posts";
            }

            if (DraftBatch is not { } batch)
            {
                return isLoadingBatches ? "Reading batches…" : "Choose the batch you counted";
            }

            var available = batch.OnHand - DraftedFromBatch;
            if (DraftOverStock)
            {
                return available <= 0
                    ? $"All {batch.OnHand:0.###} on hand are already counted"
                    : DraftedFromBatch > 0
                    ? $"Only {available:0.###} more on hand — {DraftedFromBatch:0.###} already counted"
                    : $"More than the {batch.OnHand:0.###} SAP shows on hand";
            }

            return DraftedFromBatch > 0
                ? $"of {available:0.###} still on hand · {DraftedFromBatch:0.###} already counted"
                : $"of {batch.OnHand:0.###} on hand";
        }
    }

    private bool CanAddLine =>
        !string.IsNullOrWhiteSpace(warehouseCode)
        && !string.IsNullOrWhiteSpace(draftItemCode)
        && draftQuantity > 0
        && (!draftNeedsBatch || DraftBatch is not null)
        && !DraftOverStock;

    private void StepQuantity(int step) => draftQuantity = Math.Max(0, draftQuantity + step);

    private void ToggleReason(string value) => reason = reason == value ? null : value;

    private void AddLine()
    {
        if (!CanAddLine)
        {
            return;
        }

        var batch = DraftBatch;

        // The same item and batch counted twice is one line with the quantities added, because SAP
        // checks a batch selection across the whole document and two lines naming one batch would be
        // measured together anyway.
        var existing = draft.FirstOrDefault(line => SameLine(line, draftItemCode, batch?.Number));

        if (existing is not null)
        {
            existing.Quantity += draftQuantity;
        }
        else
        {
            draft.Add(new DraftLine
            {
                ItemCode = draftItemCode!,
                ItemDescription = draftItemName,
                BatchNumber = batch?.Number,
                Quantity = draftQuantity,
                ExpiryText = batch?.ExpiryText,
                ExpirySoon = batch?.SoonText is not null,
                CheckedAgainstStock = true
            });
        }

        errorMessage = null;
        ClearDraftLine();
    }

    private static bool SameLine(DraftLine line, string? itemCode, string? batchNumber) =>
        string.Equals(line.ItemCode, itemCode, StringComparison.OrdinalIgnoreCase)
        && string.Equals(line.BatchNumber, batchNumber, StringComparison.OrdinalIgnoreCase);

    private void RemoveLine(DraftLine line) => draft.Remove(line);

    private void ClearDraftLine()
    {
        draftItemCode = null;
        draftItemName = null;
        draftBatchNumber = null;
        draftQuantity = 0;
        draftNeedsBatch = false;
        batchViews = [];
    }

    private bool HasDraft =>
        draft.Count > 0 || reason is not null || !string.IsNullOrWhiteSpace(remarks)
        || docDate is not null || draftItemCode is not null;

    private void DiscardDraft()
    {
        draft.Clear();
        ClearDraftLine();
        reason = null;
        remarks = null;
        docDate = null;
        errorMessage = null;
        attemptId = null;
    }

    // ── The preview ────────────────────────────────────────────────────────────────────────────

    private decimal DraftTotal => draft.Sum(line => line.Quantity);

    private int DraftItemCount =>
        draft.Select(line => line.ItemCode).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    private string DraftSummary =>
        $"{(DraftTotal == 1 ? "unit" : "units")} over {DraftLinesSummary}";

    private string DraftLinesSummary => $"{Plural(draft.Count, "line")} · {Plural(DraftItemCount, "item")}";

    private IEnumerable<(bool Ok, string Text)> Checklist
    {
        get
        {
            yield return string.IsNullOrEmpty(warehouseCode)
                ? (false, "Choose a warehouse")
                : (true, $"Warehouse: {warehouseCode}");
            yield return reason is null
                ? (false, "Choose a reason")
                : (true, $"Reason: {ReasonLabel(reason)}");
            yield return draft.Count == 0
                ? (false, "Count at least one line")
                : (true, $"{Plural(draft.Count, "line")} counted");

            // Copied lines never went through the batch cards, so the page has not measured them
            // against stock; SAP still will, and the checklist says which of the two is true.
            var unchecked_ = draft.Count(line => !line.CheckedAgainstStock);
            yield return unchecked_ == 0
                ? (true, "Every line within stock on hand")
                : (false, $"{Plural(unchecked_, "copied line")} checked by SAP when it posts");
        }
    }

    private string FirstMissing =>
        Checklist.Where(check => !check.Ok).Select(check => check.Text).FirstOrDefault() ?? DraftSummary;

    private bool CanReview =>
        draft.Count > 0
        && !string.IsNullOrWhiteSpace(warehouseCode)
        && !string.IsNullOrWhiteSpace(reason);

    private bool CanPost => !isPosting && CanReview;

    // ── Review and post ────────────────────────────────────────────────────────────────────────

    private void OpenReview()
    {
        if (!CanReview)
        {
            return;
        }

        errorMessage = null;
        isReviewing = true;
        focusReview = true;
    }

    private void CloseReview()
    {
        if (isPosting)
        {
            return;
        }

        isReviewing = false;
    }

    private void OnReviewKeyDown(KeyboardEventArgs args)
    {
        if (args.Key == "Escape")
        {
            CloseReview();
        }
    }

    private async Task PostAsync()
    {
        if (!CanPost)
        {
            return;
        }

        isPosting = true;
        errorMessage = null;

        // Made once and kept for the whole attempt, so a retry of a post whose reply was lost finds
        // the write-off it already raised instead of raising a second one.
        attemptId ??= Guid.NewGuid().ToString("N");

        try
        {
            var result = await Mediator.Send(new CreateStockWriteOffCommand(new CreateStockWriteOffRequest
            {
                ClientRequestId = attemptId,
                WarehouseCode = warehouseCode!,
                Reason = reason!,
                Remarks = string.IsNullOrWhiteSpace(remarks) ? null : remarks,
                DocDate = docDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Lines = draft.Select(line => new CreateStockWriteOffLine
                {
                    ItemCode = line.ItemCode,
                    ItemDescription = line.ItemDescription,
                    Quantity = line.Quantity,
                    BatchNumber = line.BatchNumber
                }).ToList()
            }));

            if (result.IsError)
            {
                errorMessage = result.FirstError.Description;
                return;
            }

            Snackbar.Add(result.Value.Message, result.Value.AlreadyPosted ? Severity.Info : Severity.Success);

            // The attempt is over either way, so the next write-off gets its own id.
            attemptId = null;
            isReviewing = false;
            draft.Clear();
            ClearDraftLine();
            reason = null;
            remarks = null;
            docDate = null;

            await LoadListAsync();
            await OpenAsync(result.Value.WriteOff.Id);
        }
        finally
        {
            isPosting = false;
        }
    }

    // ── One write-off ──────────────────────────────────────────────────────────────────────────

    private async Task OpenAsync(int id)
    {
        var result = await Mediator.Send(new GetStockWriteOffQuery(id));
        if (result.IsError)
        {
            errorMessage = result.FirstError.Description;
            return;
        }

        selected = result.Value;
    }

    private void StartNew()
    {
        selected = null;
        errorMessage = null;
    }

    /// <summary>
    /// A new draft holding what the open write-off held. A new draft rather than a second post of
    /// the same record, because a failed post whose outcome is unknown may already be in SAP — the
    /// person has to look before posting again, and a fresh draft through the review makes them.
    /// </summary>
    private void CopyAsNew()
    {
        if (selected is null)
        {
            return;
        }

        var source = selected;
        DiscardDraft();

        warehouseCode = source.WarehouseCode;
        reason = reasons.Any(row => row.Value == source.Reason) ? source.Reason : null;
        remarks = source.Remarks;

        foreach (var line in source.Lines.OrderBy(line => line.LineNum))
        {
            var existing = draft.FirstOrDefault(row => SameLine(row, line.ItemCode, line.BatchNumber));
            if (existing is not null)
            {
                existing.Quantity += line.Quantity;
                continue;
            }

            draft.Add(new DraftLine
            {
                ItemCode = line.ItemCode,
                ItemDescription = line.ItemDescription,
                BatchNumber = line.BatchNumber,
                Quantity = line.Quantity,
                CheckedAgainstStock = false
            });
        }

        selected = null;
    }

    private IEnumerable<TimelineStep> Timeline(StockWriteOffDetail writeOff)
    {
        yield return new TimelineStep("Raised", $"{ToCatDay(writeOff.CreatedAtUtc)} · {writeOff.RaisedByName}", "done");

        var attempted = writeOff.LastAttemptedAtUtc is DateTime at ? ToCatDay(at) : null;

        switch (writeOff.Status)
        {
            case "Posted":
                yield return new TimelineStep("Sent to SAP", attempted ?? "—", "done");
                yield return new TimelineStep(
                    "Written off",
                    $"{(writeOff.PostedAtUtc is DateTime posted ? ToCatDay(posted) : "—")}"
                        + (writeOff.SapDocNum is int doc ? $" · goods issue #{doc}" : string.Empty),
                    "good");
                break;
            case "PostFailed":
                yield return new TimelineStep("Sent to SAP", attempted ?? "—", "done");
                yield return new TimelineStep("Did not post", attempted ?? "—", "bad");
                break;
            case "Posting":
                yield return new TimelineStep("Sent to SAP", attempted ?? "—", "live");
                yield return new TimelineStep("Written off", "Waiting for SAP", "wait");
                break;
            default:
                yield return new TimelineStep("Sent to SAP", "Not yet", "wait");
                yield return new TimelineStep("Written off", "—", "wait");
                break;
        }
    }

    // ── Filters ────────────────────────────────────────────────────────────────────────────────

    private async Task ApplyStatusAsync(string status)
    {
        statusFilter = status;
        await LoadListAsync();
    }

    private async Task ApplyWarehouseFilterAsync(string? code)
    {
        warehouseFilter = string.IsNullOrEmpty(code) ? null : code;
        await LoadListAsync();
    }

    // ── Presentation ───────────────────────────────────────────────────────────────────────────

    private IEnumerable<(string Label, List<StockWriteOffSummary> Rows)> HistoryByDay
    {
        get
        {
            var today = ToCat(DateTime.UtcNow).Date;
            return writeOffs
                .GroupBy(row => ToCat(row.CreatedAtUtc).Date)
                .Select(group => (DayLabel(group.Key, today), group.ToList()));
        }
    }

    private static string DayLabel(DateTime day, DateTime today)
    {
        if (day == today) return "Today";
        if (day == today.AddDays(-1)) return "Yesterday";
        return day.Year == today.Year
            ? day.ToString("ddd dd MMM", CultureInfo.InvariantCulture)
            : day.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
    }

    private int CountFor(string status) =>
        string.IsNullOrEmpty(status)
            ? statusCounts.Values.Sum()
            : statusCounts.TryGetValue(status, out var count) ? count : 0;

    private string WarehouseName(string code) =>
        warehouses.FirstOrDefault(warehouse =>
            string.Equals(warehouse.WarehouseCode, code, StringComparison.OrdinalIgnoreCase))?.WarehouseName
        ?? code;

    /// <summary>The name and the code, or the code alone where the cache has no name for it.</summary>
    private string WarehouseLabel(string code)
    {
        var name = WarehouseName(code);
        return string.Equals(name, code, StringComparison.OrdinalIgnoreCase) ? code : $"{name} ({code})";
    }

    private string? ReasonLabel(string? value) =>
        value is null ? null : reasons.FirstOrDefault(row => row.Value == value)?.Description ?? value;

    private static string Plural(decimal count, string word) =>
        $"{count.ToString("0.###", CultureInfo.InvariantCulture)} {word}{(count == 1 ? "" : "s")}";

    /// <summary>
    /// The Nocturne family for a status, named once so the pill in the list and the pill on the
    /// detail cannot drift apart.
    /// </summary>
    private static string FamilyFor(string status) => status switch
    {
        "Pending" => "accent",
        "Posting" => "info",
        "Posted" => "good",
        "PostFailed" => "bad",
        _ => "neutral"
    };

    private static string LabelFor(string status) => status switch
    {
        "Pending" => "To post",
        "Posting" => "Posting",
        "Posted" => "Written off",
        "PostFailed" => "Failed",
        "Cancelled" => "Cancelled",
        _ => status
    };

    private static DateTime ToCat(DateTime utc) => IAuditService
        .ToCAT(utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc));

    private static string ToCatTime(DateTime utc) =>
        ToCat(utc).ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string ToCatDate(DateTime utc) =>
        ToCat(utc).ToString("dd MMM yyyy", CultureInfo.InvariantCulture);

    private static string ToCatShort(DateTime utc) =>
        DayLabel(ToCat(utc).Date, ToCat(DateTime.UtcNow).Date).Replace("Today", "today").Replace("Yesterday", "yesterday")
        + " " + ToCatTime(utc);

    private static string ToCatDay(DateTime utc)
    {
        var today = ToCat(DateTime.UtcNow).Date;
        return $"{DayLabel(ToCat(utc).Date, today)} {ToCatTime(utc)}";
    }

    /// <summary>A line on the count, carrying what the page learned about its batch.</summary>
    private sealed class DraftLine
    {
        public string ItemCode { get; init; } = string.Empty;
        public string? ItemDescription { get; init; }
        public string? BatchNumber { get; init; }
        public decimal Quantity { get; set; }
        public string? ExpiryText { get; init; }
        public bool ExpirySoon { get; init; }

        /// <summary>
        /// True when the quantity was measured against SAP's batch stock as it was added. Lines
        /// copied from an earlier write-off were not.
        /// </summary>
        public bool CheckedAgainstStock { get; init; }
    }

    /// <summary>A batch as the cards show it: what it holds and how near its date it is.</summary>
    private sealed record BatchView(string Number, decimal OnHand, DateTime? ExpiresOn, string? ExpiryText, string? SoonText, bool IsExpired)
    {
        public static BatchView From(BatchDto batch)
        {
            // SAP's date comes through as text; an unreadable one is shown as sent and never flagged.
            DateTime? expires = DateTime.TryParse(batch.ExpiryDate, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out var parsed)
                ? parsed.Date
                : null;

            var text = expires?.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)
                ?? (string.IsNullOrWhiteSpace(batch.ExpiryDate) ? null : batch.ExpiryDate);

            string? soon = null;
            var expired = false;
            if (expires is DateTime date)
            {
                var days = (date - ToCat(DateTime.UtcNow).Date).Days;
                expired = days < 0;
                soon = days switch
                {
                    < 0 => "expired",
                    0 => "today",
                    1 => "tomorrow",
                    <= ExpirySoonDays => $"in {days} days",
                    _ => null
                };
            }

            return new BatchView(batch.BatchNumber!, batch.Quantity, expires, text, soon, expired);
        }
    }

    private sealed record TimelineStep(string Label, string Detail, string State);
}
