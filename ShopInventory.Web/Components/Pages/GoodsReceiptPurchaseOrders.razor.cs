using MediatR;
using Microsoft.AspNetCore.Components;
using ShopInventory.Web.Data;
using ShopInventory.Web.Features.GoodsReceiptPurchaseOrders.Queries.GetGoodsReceiptPurchaseOrders;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

public partial class GoodsReceiptPurchaseOrders
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IMasterDataCacheService CacheService { get; set; } = default!;
    [Inject] private IAuditService AuditService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ILogger<GoodsReceiptPurchaseOrders> Logger { get; set; } = default!;

    private GoodsReceiptPurchaseOrderListResponse? goodsReceiptResponse;
    private GoodsReceiptPurchaseOrderDto? selectedGoodsReceipt;
    private List<BusinessPartnerDto> suppliers = new();
    private BusinessPartnerDto? selectedSupplier;
    private bool isLoading = true;
    private bool hasInitialized;
    private bool hasLoggedView;
    private bool showSupplierResults;
    private string? errorMessage;
    private string supplierFilter = string.Empty;
    private string supplierSearchTerm = string.Empty;
    private DateTime? fromDate = DateTime.Today.AddDays(-30);
    private DateTime? toDate = DateTime.Today;
    private int currentPage = 1;
    private const int PageSize = 20;

    private List<GoodsReceiptPurchaseOrderDto> GoodsReceipts => goodsReceiptResponse?.GoodsReceipts ?? new List<GoodsReceiptPurchaseOrderDto>();
    private IEnumerable<BusinessPartnerDto> FilteredSuppliers => suppliers
        .Where(supplier =>
            !string.IsNullOrWhiteSpace(supplier.CardCode) &&
            (string.IsNullOrWhiteSpace(supplierSearchTerm) ||
             supplier.CardCode.Contains(supplierSearchTerm, StringComparison.OrdinalIgnoreCase) ||
             (supplier.CardName?.Contains(supplierSearchTerm, StringComparison.OrdinalIgnoreCase) ?? false)))
        .Take(12)
        .ToList();
    private int CurrentPageCount => goodsReceiptResponse?.Count ?? GoodsReceipts.Count;
    private int OpenGoodsReceiptCount => GoodsReceipts.Count(goodsReceipt => string.Equals(goodsReceipt.DocStatus, "Open", StringComparison.OrdinalIgnoreCase));
    private decimal CurrentPageTotal => GoodsReceipts.Sum(goodsReceipt => goodsReceipt.DocTotal);

    // The window the page opened on, stated in the sticky bar: the figures below
    // are all "in this window", and once the hero has scrolled away nothing else
    // says which window that is.
    private string DateRangeLabel =>
        $"{fromDate?.ToString("dd MMM yyyy") ?? "Any"} – {toDate?.ToString("dd MMM yyyy") ?? "Any"}";

    private string RegisterCountText => goodsReceiptResponse is null
        ? "Not loaded"
        : $"{CurrentPageCount:N0} of {goodsReceiptResponse.TotalCount:N0}";

    // SAP is asked for one page at a time, so the foot counts the rows on screen
    // against the whole result rather than numbering pages it has not seen.
    private string PageRangeText
    {
        get
        {
            if (CurrentPageCount == 0)
                return "No documents";

            var start = ((currentPage - 1) * PageSize) + 1;
            return $"Showing {start:N0}–{start + CurrentPageCount - 1:N0} of {goodsReceiptResponse?.TotalCount ?? CurrentPageCount:N0}";
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || hasInitialized)
            return;

        hasInitialized = true;
        await LoadSuppliersAsync();
        await LoadGoodsReceiptsAsync();
        StateHasChanged();
    }

    private async Task LoadSuppliersAsync()
    {
        try
        {
            suppliers = await CacheService.GetBusinessPartnersAsync();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to load supplier cache for goods receipt PO filters");
        }
    }

    private async Task LoadGoodsReceiptsAsync()
    {
        isLoading = true;
        errorMessage = null;

        try
        {
            var result = await Mediator.Send(new GetGoodsReceiptPurchaseOrdersQuery(
                currentPage,
                PageSize,
                string.IsNullOrWhiteSpace(supplierFilter) ? null : supplierFilter.Trim(),
                fromDate,
                toDate));

            result.SwitchFirst(
                value =>
                {
                    goodsReceiptResponse = value;
                    // Cleared rather than set to the first row: the detail is a
                    // drawer now, and pre-selecting would open it over the
                    // register on every load and every page turn.
                    selectedGoodsReceipt = null;
                },
                error =>
                {
                    goodsReceiptResponse = new GoodsReceiptPurchaseOrderListResponse
                    {
                        Page = currentPage,
                        PageSize = PageSize
                    };
                    selectedGoodsReceipt = null;
                    errorMessage = error.Description;
                });

            if (!result.IsError && !hasLoggedView)
            {
                hasLoggedView = true;
                await AuditService.LogAsync(AuditActions.ViewGoodsReceiptPurchaseOrders, "GoodsReceiptPurchaseOrder", null);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load goods receipt PO page");
            goodsReceiptResponse = new GoodsReceiptPurchaseOrderListResponse
            {
                Page = currentPage,
                PageSize = PageSize
            };
            selectedGoodsReceipt = null;
            errorMessage = "Failed to load goods receipt POs.";
        }
        finally
        {
            isLoading = false;
        }
    }

    private void SelectGoodsReceipt(GoodsReceiptPurchaseOrderDto goodsReceipt)
    {
        selectedGoodsReceipt = goodsReceipt;
    }

    private void CloseDetail() => selectedGoodsReceipt = null;

    private async Task SearchAsync()
    {
        if (!TryResolveSupplierFilter())
            return;

        currentPage = 1;
        await LoadGoodsReceiptsAsync();
    }

    private async Task ClearFiltersAsync()
    {
        supplierFilter = string.Empty;
        supplierSearchTerm = string.Empty;
        selectedSupplier = null;
        showSupplierResults = false;
        fromDate = DateTime.Today.AddDays(-30);
        toDate = DateTime.Today;
        currentPage = 1;
        await LoadGoodsReceiptsAsync();
    }

    private async Task ReloadAsync() => await LoadGoodsReceiptsAsync();

    private async Task PreviousPageAsync()
    {
        if (currentPage <= 1)
            return;

        currentPage--;
        await LoadGoodsReceiptsAsync();
    }

    private async Task NextPageAsync()
    {
        if (!(goodsReceiptResponse?.HasMore ?? false))
            return;

        currentPage++;
        await LoadGoodsReceiptsAsync();
    }

    private void NavigateToCreate() => NavigationManager.NavigateTo("/goods-receipt-pos/create");
    private void ShowSupplierResults() => showSupplierResults = true;

    private async Task HandleSupplierBlur()
    {
        await Task.Delay(150);
        showSupplierResults = false;
    }

    private void SelectSupplier(BusinessPartnerDto supplier)
    {
        selectedSupplier = supplier;
        supplierFilter = supplier.CardCode ?? string.Empty;
        supplierSearchTerm = supplier.DisplayName;
        showSupplierResults = false;
        errorMessage = null;
    }

    private void ClearSupplier()
    {
        selectedSupplier = null;
        supplierFilter = string.Empty;
        supplierSearchTerm = string.Empty;
        showSupplierResults = true;
        errorMessage = null;
    }

    private bool TryResolveSupplierFilter()
    {
        var searchTerm = supplierSearchTerm.Trim();

        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            supplierFilter = string.Empty;
            selectedSupplier = null;
            errorMessage = null;
            return true;
        }

        if (selectedSupplier is not null &&
            (string.Equals(selectedSupplier.DisplayName, searchTerm, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(selectedSupplier.CardCode, searchTerm, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(selectedSupplier.CardName, searchTerm, StringComparison.OrdinalIgnoreCase)))
        {
            supplierFilter = selectedSupplier.CardCode ?? string.Empty;
            errorMessage = null;
            return true;
        }

        var exactCodeMatch = suppliers.FirstOrDefault(supplier =>
            string.Equals(supplier.CardCode, searchTerm, StringComparison.OrdinalIgnoreCase));
        if (exactCodeMatch is not null)
        {
            SelectSupplier(exactCodeMatch);
            return true;
        }

        var exactNameMatches = suppliers
            .Where(supplier =>
                string.Equals(supplier.CardName, searchTerm, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(supplier.DisplayName, searchTerm, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        if (exactNameMatches.Count == 1)
        {
            SelectSupplier(exactNameMatches[0]);
            return true;
        }

        var partialMatches = suppliers
            .Where(supplier =>
                (supplier.CardCode?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (supplier.CardName?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false))
            .Take(2)
            .ToList();
        if (partialMatches.Count == 1)
        {
            SelectSupplier(partialMatches[0]);
            return true;
        }

        if (partialMatches.Count > 1)
        {
            errorMessage = "Select a supplier from the suggestions to filter goods receipt POs by name.";
            showSupplierResults = true;
            return false;
        }

        supplierFilter = searchTerm;
        selectedSupplier = null;
        errorMessage = null;
        return true;
    }

    // The badge takes its hue from a family token in purchase-documents.css, so
    // what this returns is the family class rather than a status name: open is
    // the good family, closed the neutral one, cancelled the bad one.
    private static string GetStatusCssClass(string? status)
    {
        if (string.Equals(status, "Closed", StringComparison.OrdinalIgnoreCase))
            return "pdx-fam-neutral";
        if (string.Equals(status, "Cancelled", StringComparison.OrdinalIgnoreCase))
            return "pdx-fam-bad";
        return "pdx-fam-good";
    }
}