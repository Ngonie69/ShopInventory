using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using ShopInventory.Web.Data;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

public partial class Products : IDisposable
{
    private static readonly int[] StandardPageSizes = [10, 25, 50, 100, 200];

    /// <summary>
    /// The offered sizes, always including the one actually in force. The configured
    /// default is not necessarily one of the standard steps — 20 is — and a select
    /// whose options exclude the current value renders as some other number while the
    /// page shows a different count of rows.
    /// </summary>
    private IEnumerable<int> PageSizes => StandardPageSizes.Contains(pageSize)
        ? StandardPageSizes
        : StandardPageSizes.Append(pageSize).Order();

    private IEnumerable<NocturneSelectOption<int>> PageSizeSelectOptions =>
        PageSizes.Select(size => new NocturneSelectOption<int>(size, size.ToString()));

    private IEnumerable<NocturneSelectOption<string>> WarehouseSelectOptions =>
        warehouses
            .Where(w => !string.IsNullOrWhiteSpace(w.WarehouseCode))
            .Select(w => new NocturneSelectOption<string>(w.WarehouseCode!, w.DisplayName));

    // Long enough that typing an item code does not fire a query per keystroke,
    // short enough that the list still feels like it is following the field.
    private static readonly TimeSpan FilterDebounce = TimeSpan.FromMilliseconds(300);

    private enum NoticeLevel
    {
        Info,
        Warning,
        Error
    }

    [Inject] private IProductService ProductService { get; set; } = null!;
    [Inject] private IMasterDataCacheService MasterDataCache { get; set; } = null!;
    [Inject] private IAppSettingsProvider AppSettings { get; set; } = null!;
    [Inject] private IAuditService AuditService { get; set; } = null!;
    [Inject] private ILogger<Products> Logger { get; set; } = null!;

    // ── Warehouse ───────────────────────────────────────────────────────────

    private List<WarehouseDto> warehouses = [];
    private WarehouseDto? selectedWarehouse;
    private bool isLoadingWarehouses = true;
    private bool hasLoadedWarehouses;

    // ── List ────────────────────────────────────────────────────────────────

    private List<ProductDto>? products;
    private WarehouseStockSummary? summary;
    private DateTime? lastSyncedAt;

    private int currentPage = 1;
    private int pageSize = 20;

    /// <summary>Rows matching the current filter across the whole warehouse.</summary>
    private int matchingCount;

    /// <summary>
    /// True when <see cref="matchingCount"/> is a floor rather than a total — the
    /// API fallback path counts only the page it fetched, so the figure is rendered
    /// as "20+" rather than claimed as the whole warehouse.
    /// </summary>
    private bool countIsFloor;

    private bool hasMore;
    private bool hasSearched;
    private bool isLoading;
    private bool isRefreshing;

    /// <summary>
    /// True while the rows are the answer to a barcode scan rather than a page of the
    /// warehouse — the count beside them describes the match, not the warehouse.
    /// </summary>
    private bool showingBarcodeMatch;

    private string filterTerm = string.Empty;
    private string? barcode;

    private string? notice;
    private NoticeLevel noticeLevel = NoticeLevel.Error;

    // ── Drawer ──────────────────────────────────────────────────────────────

    private ProductDto? selectedProduct;
    private List<BatchDto>? batches;
    private string? batchError;
    private bool isLoadingBatches;

    // ── Cancellation and timing ─────────────────────────────────────────────

    private CancellationTokenSource? searchCts;
    private CancellationTokenSource? filterCts;
    private CancellationTokenSource? batchCts;
    private System.Threading.Timer? loadingTimer;
    private int loadingSeconds;

    // ── Lifecycle ───────────────────────────────────────────────────────────

    protected override void OnInitialized()
    {
        pageSize = AppSettings.PageSize;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || hasLoadedWarehouses)
            return;

        hasLoadedWarehouses = true;
        await LoadWarehousesAsync();
        await AuditService.LogAsync(AuditActions.ViewProducts, "Product", null);
        StateHasChanged();
    }

    private async Task LoadWarehousesAsync()
    {
        isLoadingWarehouses = true;
        try
        {
            warehouses = await MasterDataCache.GetWarehousesAsync() ?? [];

            if (warehouses.Count > 0)
            {
                selectedWarehouse = warehouses.FirstOrDefault(w => w.WarehouseCode == "01") ?? warehouses[0];
                await LoadWarehouseContextAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Products page: failed to load warehouses");
            SetNotice($"Warehouses could not be loaded: {ex.Message}", NoticeLevel.Error);
        }
        finally
        {
            isLoadingWarehouses = false;
        }
    }

    // ── Warehouse selection ─────────────────────────────────────────────────

    private async Task OnWarehouseChanged(string? code)
    {
        selectedWarehouse = warehouses.FirstOrDefault(w => w.WarehouseCode == code);

        // The results on screen belong to the warehouse that was selected a moment
        // ago; keeping them under a new warehouse's name would be the same class of
        // bug as the figures this page was rebuilt to fix.
        products = null;
        summary = null;
        lastSyncedAt = null;
        matchingCount = 0;
        countIsFloor = false;
        hasMore = false;
        showingBarcodeMatch = false;
        currentPage = 1;
        hasSearched = false;
        notice = null;
        CloseDrawer();

        if (selectedWarehouse is not null)
            await LoadWarehouseContextAsync();
    }

    /// <summary>Figures and sync time for the selected warehouse, no rows.</summary>
    private async Task LoadWarehouseContextAsync()
    {
        if (selectedWarehouse?.WarehouseCode is not { } code)
            return;

        try
        {
            summary = await ProductService.GetStockSummaryAsync(code);
            lastSyncedAt = await ProductService.GetLastSyncedAtAsync(code);
        }
        catch (Exception ex)
        {
            // The figures are a supporting detail; losing them should not stop the
            // page from listing stock.
            Logger.LogWarning(ex, "Products page: could not read stock summary for {WarehouseCode}", code);
            summary = null;
            lastSyncedAt = null;
        }
    }

    // ── Loading rows ────────────────────────────────────────────────────────

    private Task SearchProducts()
    {
        currentPage = 1;
        return LoadPageAsync();
    }

    private async Task LoadPageAsync()
    {
        if (selectedWarehouse?.WarehouseCode is not { } warehouseCode)
            return;

        searchCts?.Cancel();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        searchCts = cts;

        isLoading = true;
        hasSearched = true;
        showingBarcodeMatch = false;
        notice = null;
        StartLoadingTimer();
        StateHasChanged();

        try
        {
            var response = await ProductService.GetPagedProductsAsync(
                warehouseCode,
                currentPage,
                pageSize,
                filterTerm,
                cts.Token);

            if (response is null)
            {
                products = null;
                matchingCount = 0;
                countIsFloor = false;
                hasMore = false;
                SetNotice(
                    "No stock came back for this warehouse. It may be empty, or the server may be unavailable.",
                    NoticeLevel.Warning);
                return;
            }

            products = response.Products ?? [];
            matchingCount = response.Count;
            hasMore = response.HasMore;

            // The cache path counts every matching row; the API fallback counts only
            // the page it fetched. When more rows exist beyond a count that is no
            // larger than what has been paged through, the count is the second kind.
            countIsFloor = response.HasMore && response.Count <= currentPage * pageSize;

            await LoadWarehouseContextAsync();
        }
        catch (OperationCanceledException)
        {
            // Either the 90s ceiling or the user's own Cancel; CancelSearch has
            // already written its own notice in the second case.
            products = null;
            if (notice is null)
                SetNotice("The stock query took too long and was stopped. Try again.", NoticeLevel.Warning);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Products page: error loading stock for {WarehouseCode}", warehouseCode);
            products = null;
            SetNotice($"Stock could not be loaded: {ex.Message}", NoticeLevel.Error);
        }
        finally
        {
            isLoading = false;
            StopLoadingTimer();
            StateHasChanged();
        }
    }

    private async Task SearchByBarcode()
    {
        if (selectedWarehouse?.WarehouseCode is not { } warehouseCode || string.IsNullOrWhiteSpace(barcode))
            return;

        var scanned = barcode.Trim();

        searchCts?.Cancel();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        searchCts = cts;

        isLoading = true;
        hasSearched = true;
        notice = null;
        StartLoadingTimer();
        StateHasChanged();

        try
        {
            var product = await ProductService.SearchProductByBarcodeAsync(scanned, warehouseCode, cts.Token);

            if (product is null)
            {
                products = [];
                matchingCount = 0;
                countIsFloor = false;
                hasMore = false;
                showingBarcodeMatch = true;
                SetNotice(
                    $"Nothing in {warehouseCode} carries the code “{scanned}”.",
                    NoticeLevel.Warning);
                return;
            }

            // A scan is a jump to one item, so it replaces the list rather than
            // filtering it — and the filter box is cleared to say so.
            products = [product];
            filterTerm = string.Empty;
            matchingCount = 1;
            countIsFloor = false;
            hasMore = false;
            showingBarcodeMatch = true;
            currentPage = 1;
        }
        catch (OperationCanceledException)
        {
            if (notice is null)
                SetNotice("The barcode lookup took too long and was stopped. Try again.", NoticeLevel.Warning);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Products page: error looking up barcode {Barcode}", scanned);
            SetNotice($"Barcode lookup failed: {ex.Message}", NoticeLevel.Error);
        }
        finally
        {
            isLoading = false;
            StopLoadingTimer();
            StateHasChanged();
        }
    }

    private async Task OnBarcodeKeyDown(KeyboardEventArgs args)
    {
        // Most scanners send Enter after the code, so the field submits itself.
        if (args.Key is "Enter" or "NumpadEnter" && !string.IsNullOrWhiteSpace(barcode))
            await SearchByBarcode();
    }

    private void CancelSearch()
    {
        searchCts?.Cancel();
        isLoading = false;
        StopLoadingTimer();
        SetNotice("Search cancelled.", NoticeLevel.Info);
    }

    private async Task RefreshStock()
    {
        if (selectedWarehouse?.WarehouseCode is not { } warehouseCode)
            return;

        isRefreshing = true;
        notice = null;
        StateHasChanged();

        try
        {
            var synced = await ProductService.RefreshStockAsync(warehouseCode);
            if (!synced)
            {
                SetNotice($"The stock sync for {warehouseCode} did not complete. Try again shortly.",
                    NoticeLevel.Warning);
            }

            await LoadWarehouseContextAsync();

            if (hasSearched)
                await LoadPageAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Products page: error refreshing stock for {WarehouseCode}", warehouseCode);
            SetNotice($"Stock sync failed: {ex.Message}", NoticeLevel.Error);
        }
        finally
        {
            isRefreshing = false;
            StateHasChanged();
        }
    }

    // ── Filter ──────────────────────────────────────────────────────────────

    private async Task OnFilterInput(ChangeEventArgs args)
    {
        filterTerm = args.Value?.ToString() ?? string.Empty;

        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref filterCts, cts)?.Cancel();

        try
        {
            await Task.Delay(FilterDebounce, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a later keystroke.
            return;
        }

        currentPage = 1;
        await LoadPageAsync();
    }

    private async Task ClearFilter()
    {
        filterTerm = string.Empty;
        currentPage = 1;
        await LoadPageAsync();
    }

    private void ClearSearch()
    {
        searchCts?.Cancel();
        Interlocked.Exchange(ref filterCts, null)?.Cancel();

        barcode = null;
        filterTerm = string.Empty;
        products = null;
        matchingCount = 0;
        countIsFloor = false;
        hasMore = false;
        showingBarcodeMatch = false;
        currentPage = 1;
        hasSearched = false;
        notice = null;
        CloseDrawer();
    }

    // ── Paging ──────────────────────────────────────────────────────────────

    private int PageCount => Math.Max(1, (int)Math.Ceiling(matchingCount / (double)pageSize));

    private bool CanGoNext => hasMore || currentPage < PageCount;

    private string CountLabel
    {
        get
        {
            var shown = products?.Count ?? 0;

            // Each phrasing names the set the figure counts. The old page said
            // "26 products" for whatever it happened to hold, which was the page,
            // the filtered subset and the warehouse by turns.
            if (showingBarcodeMatch)
                return shown == 1 ? "Barcode match" : "No barcode match";

            var total = countIsFloor ? $"{matchingCount:N0}+" : matchingCount.ToString("N0");

            if (!string.IsNullOrWhiteSpace(filterTerm))
                return $"{shown:N0} shown · {total} matching";

            return $"{shown:N0} shown · {total} in warehouse";
        }
    }

    private async Task GoToPage(int page)
    {
        if (page < 1 || page == currentPage)
            return;

        currentPage = page;
        await LoadPageAsync();
    }

    private async Task OnPageSizeChanged(int size)
    {
        if (size > 0)
            pageSize = size;

        currentPage = 1;
        await LoadPageAsync();
    }

    // The design's "1 2 3 … 101": the ends, the current page and its neighbours,
    // with a gap standing in for everything skipped.
    private static List<int?> PageSlots(int current, int pages)
    {
        var slots = new List<int?>();
        var last = 0;

        for (var page = 1; page <= pages; page++)
        {
            var keep = page == 1 || page == pages || Math.Abs(page - current) <= 1;
            if (!keep)
                continue;

            if (last > 0 && page - last > 1)
                slots.Add(null);

            slots.Add(page);
            last = page;
        }

        return slots;
    }

    // ── Drawer ──────────────────────────────────────────────────────────────

    private async Task ViewProduct(ProductDto product)
    {
        selectedProduct = product;
        batches = null;
        batchError = null;
        await LoadBatchesAsync(product);
    }

    private void CloseDrawer()
    {
        batchCts?.Cancel();
        selectedProduct = null;
        batches = null;
        batchError = null;
        isLoadingBatches = false;
    }

    /// <summary>
    /// Batches are read per item when the drawer opens. The rows in the list come
    /// from the warehouse stock cache, which is built from OITM/OITW and carries no
    /// batch data at all — so nothing on the row can answer this, and the page must
    /// ask SAP.
    /// </summary>
    private async Task LoadBatchesAsync(ProductDto product)
    {
        if (selectedWarehouse?.WarehouseCode is not { } warehouseCode ||
            string.IsNullOrWhiteSpace(product.ItemCode))
        {
            return;
        }

        var itemCode = product.ItemCode;

        batchCts?.Cancel();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        batchCts = cts;

        isLoadingBatches = true;
        batchError = null;
        batches = null;
        StateHasChanged();

        try
        {
            var response = await ProductService.GetProductBatchesAsync(itemCode, warehouseCode, cts.Token);

            // The drawer may have moved to another item while SAP was answering.
            if (selectedProduct?.ItemCode != itemCode)
                return;

            if (response is null)
                batchError = "Batch information could not be read from SAP.";
            else
                batches = response.Batches ?? [];
        }
        catch (OperationCanceledException)
        {
            if (selectedProduct?.ItemCode == itemCode)
                batchError = "Reading batches from SAP took too long.";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Products page: error loading batches for {ItemCode} in {WarehouseCode}",
                itemCode, warehouseCode);
            if (selectedProduct?.ItemCode == itemCode)
                batchError = $"Batches could not be read: {ex.Message}";
        }
        finally
        {
            if (selectedProduct?.ItemCode == itemCode)
                isLoadingBatches = false;
            StateHasChanged();
        }
    }

    // ── Formatting ──────────────────────────────────────────────────────────

    /// <summary>
    /// What can actually leave the warehouse today: on hand, less what customer
    /// orders have already claimed. Deliberately *not* SAP's
    /// <see cref="ProductDto.QuantityAvailable"/>, which also counts stock still on
    /// a purchase order and so answers a different question.
    /// </summary>
    /// <remarks>
    /// Derived here rather than stored, so it cannot drift from the two figures it
    /// is computed from — both of which are on screen beside it.
    /// </remarks>
    private static decimal FreeToSell(ProductDto product)
        => product.QuantityInStock - product.QuantityCommitted;

    private static string Qty(decimal quantity, string? uomCode) => QuantityDisplay.Format(quantity, uomCode);

    /// <summary>
    /// The three states a stock figure can be in. Below zero is not the same as
    /// zero — it means commitments exceed the stock behind them — so it takes the
    /// notice ink rather than dimming away with the empty rows.
    /// </summary>
    private static string StockClass(decimal quantity) => quantity switch
    {
        < 0 => "pr-qty-short",
        0 => "pr-qty-zero",
        _ => string.Empty
    };

    // Committed and on-order are claims on the item rather than plain counts, so a
    // non-zero one takes the accent and a zero recedes with everything else.
    private static string ClaimClass(decimal quantity) => quantity > 0 ? "pr-qty-claim" : "pr-qty-zero";

    private static string BatchStatusClass(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "released" => string.Empty,
        "not accessible" => "pr-status-held",
        "locked" => "pr-status-locked",
        _ => "pr-status-held"
    };

    // SAP hands these back as strings, and an unset date arrives as a zero-ish
    // placeholder rather than an empty one.
    private static string BatchDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "—";

        return DateTime.TryParse(raw, out var parsed)
            ? parsed.ToString("d MMM yyyy")
            : raw;
    }

    private void SetNotice(string message, NoticeLevel level)
    {
        notice = message;
        noticeLevel = level;
    }

    // ── Loading clock ───────────────────────────────────────────────────────

    private void StartLoadingTimer()
    {
        loadingSeconds = 0;
        loadingTimer?.Dispose();
        loadingTimer = new System.Threading.Timer(async _ =>
        {
            loadingSeconds++;
            await InvokeAsync(StateHasChanged);
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private void StopLoadingTimer()
    {
        loadingTimer?.Dispose();
        loadingTimer = null;
        loadingSeconds = 0;
    }

    public void Dispose()
    {
        searchCts?.Cancel();
        searchCts?.Dispose();
        filterCts?.Cancel();
        filterCts?.Dispose();
        batchCts?.Cancel();
        batchCts?.Dispose();
        loadingTimer?.Dispose();
    }
}
