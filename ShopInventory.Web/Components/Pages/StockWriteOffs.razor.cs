using System.Globalization;
using MediatR;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using ShopInventory.Web.Features.StockWriteOffs.Commands.CreateStockWriteOff;
using ShopInventory.Web.Features.StockWriteOffs.Queries.GetStockWriteOff;
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
/// time, because a caller that is not this page still has to be told.
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

    private readonly List<StockWriteOffSummary> writeOffs = [];
    private readonly List<CreateStockWriteOffLine> draft = [];
    private Dictionary<string, int> statusCounts = [];

    private StockWriteOffDetail? selected;
    private string? errorMessage;
    private string statusFilter = string.Empty;

    private bool hasInitialized;
    private bool isLoadingList = true;
    private bool isLoadingItems;
    private bool isLoadingBatches;
    private bool isPosting;

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
    private List<ProductDto> items = [];
    private List<BatchDto> batches = [];
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
        ("Posted", "Written off"),
        ("PostFailed", "Failed")
    ];

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || hasInitialized)
        {
            return;
        }

        hasInitialized = true;

        await Task.WhenAll(LoadListAsync(), LoadWarehousesAsync(), LoadReasonsAsync());
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

    private IEnumerable<INocturneSelectOption<string>> ReasonOptions =>
        reasons.Select(row => new NocturneSelectOption<string>(row.Value, row.Description));

    private IReadOnlyList<NocturnePickerOption> ItemOptions =>
        items
            .Where(item => !string.IsNullOrWhiteSpace(item.ItemCode))
            .Select(item => new NocturnePickerOption(
                item.ItemCode!,
                item.ItemName ?? item.ItemCode!,
                item.ItemCode))
            .ToList();

    private IEnumerable<INocturneSelectOption<string>> BatchOptions =>
        batches
            .Where(batch => !string.IsNullOrWhiteSpace(batch.BatchNumber))
            .Select(batch => new NocturneSelectOption<string>(batch.BatchNumber!, batch.BatchNumber!)
            {
                // The quantity on hand and the expiry are what decide which batch is the one being
                // written off, so they belong in the row rather than a tooltip.
                Hint = FormatBatchHint(batch)
            });

    private static string FormatBatchHint(BatchDto batch)
    {
        var hint = $"{batch.Quantity.ToString("0.###", CultureInfo.InvariantCulture)} on hand";
        return string.IsNullOrWhiteSpace(batch.ExpiryDate) ? hint : $"{hint} · exp {batch.ExpiryDate}";
    }

    // ── Loading ────────────────────────────────────────────────────────────────────────────────

    private async Task LoadListAsync()
    {
        isLoadingList = true;
        try
        {
            var result = await Mediator.Send(new GetStockWriteOffsQuery(
                string.IsNullOrEmpty(statusFilter) ? null : statusFilter, null, 1, PageSize));

            writeOffs.Clear();
            if (result.IsError)
            {
                errorMessage = result.FirstError.Description;
                return;
            }

            writeOffs.AddRange(result.Value.Items);
            statusCounts = result.Value.StatusCounts;
        }
        finally
        {
            isLoadingList = false;
        }
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

    private async Task OnWarehouseChangedAsync(string? code)
    {
        warehouseCode = code;

        // The lines already counted belong to the warehouse they were counted at, so changing it
        // clears them rather than quietly moving them somewhere they were never counted.
        draft.Clear();
        ClearDraftLine();

        items = [];
        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        isLoadingItems = true;
        try
        {
            items = await MasterData.GetProductsAsync(code);
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Could not load the items in warehouse {Warehouse}", code);
            errorMessage = $"The items in {code} could not be read, so nothing can be counted against it yet.";
        }
        finally
        {
            isLoadingItems = false;
        }
    }

    private async Task OnDraftItemChangedAsync(string? itemCode)
    {
        draftItemCode = itemCode;
        draftBatchNumber = null;
        batches = [];
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
            batches = response?.Batches?.Where(batch => batch.Quantity > 0).ToList() ?? [];

            if (batches.Count == 0)
            {
                errorMessage = $"SAP shows no batch of {itemCode} holding stock in {warehouseCode}, "
                    + "so there is nothing here to write off.";
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

    // ── The bench ──────────────────────────────────────────────────────────────────────────────

    private bool CanAddLine =>
        !string.IsNullOrWhiteSpace(warehouseCode)
        && !string.IsNullOrWhiteSpace(draftItemCode)
        && draftQuantity > 0
        && (!draftNeedsBatch || !string.IsNullOrWhiteSpace(draftBatchNumber));

    private bool CanPost =>
        !isPosting
        && draft.Count > 0
        && !string.IsNullOrWhiteSpace(warehouseCode)
        && !string.IsNullOrWhiteSpace(reason);

    private void AddLine()
    {
        if (!CanAddLine)
        {
            return;
        }

        // The same item and batch counted twice is one line with the quantities added, because SAP
        // checks a batch selection across the whole document and two lines naming one batch would be
        // measured together anyway.
        var existing = draft.FirstOrDefault(line =>
            string.Equals(line.ItemCode, draftItemCode, StringComparison.OrdinalIgnoreCase)
            && string.Equals(line.BatchNumber, draftBatchNumber, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.Quantity += draftQuantity;
        }
        else
        {
            draft.Add(new CreateStockWriteOffLine
            {
                ItemCode = draftItemCode!,
                ItemDescription = draftItemName,
                Quantity = draftQuantity,
                BatchNumber = string.IsNullOrWhiteSpace(draftBatchNumber) ? null : draftBatchNumber
            });
        }

        errorMessage = null;
        ClearDraftLine();
    }

    private void RemoveLine(CreateStockWriteOffLine line) => draft.Remove(line);

    private void ClearDraftLine()
    {
        draftItemCode = null;
        draftItemName = null;
        draftBatchNumber = null;
        draftQuantity = 0;
        draftNeedsBatch = false;
        batches = [];
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
                Lines = draft.ToList()
            }));

            if (result.IsError)
            {
                errorMessage = result.FirstError.Description;
                return;
            }

            Snackbar.Add(result.Value.Message, result.Value.AlreadyPosted ? Severity.Info : Severity.Success);

            // The attempt is over either way, so the next write-off gets its own id.
            attemptId = null;
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

    private async Task ApplyStatusAsync(string status)
    {
        statusFilter = status;
        await LoadListAsync();
    }

    // ── Presentation ───────────────────────────────────────────────────────────────────────────

    private int CountFor(string status) =>
        string.IsNullOrEmpty(status)
            ? statusCounts.Values.Sum()
            : statusCounts.TryGetValue(status, out var count) ? count : 0;

    /// <summary>
    /// The Nocturne family for a status, named once so the pill in the list and the pill on the
    /// bench cannot drift apart.
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

    private static string ToCat(DateTime utc) => IAuditService
        .ToCAT(utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc))
        .ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture);
}
