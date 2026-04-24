using MediatR;
using Microsoft.AspNetCore.Components;
using ShopInventory.Web.Data;
using ShopInventory.Web.Features.PurchaseInvoices.Queries.GetPurchaseInvoices;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

public partial class PurchaseInvoices
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IMasterDataCacheService CacheService { get; set; } = default!;
    [Inject] private IAuditService AuditService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ILogger<PurchaseInvoices> Logger { get; set; } = default!;

    private PurchaseInvoiceListResponse? invoiceResponse;
    private PurchaseInvoiceDto? selectedInvoice;
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

    private List<PurchaseInvoiceDto> Invoices => invoiceResponse?.Invoices ?? new List<PurchaseInvoiceDto>();
    private IEnumerable<BusinessPartnerDto> FilteredSuppliers => suppliers
        .Where(supplier =>
            !string.IsNullOrWhiteSpace(supplier.CardCode) &&
            (string.IsNullOrWhiteSpace(supplierSearchTerm) ||
             supplier.CardCode.Contains(supplierSearchTerm, StringComparison.OrdinalIgnoreCase) ||
             (supplier.CardName?.Contains(supplierSearchTerm, StringComparison.OrdinalIgnoreCase) ?? false)))
        .Take(12)
        .ToList();
    private int CurrentPageCount => invoiceResponse?.Count ?? Invoices.Count;
    private int OpenInvoiceCount => Invoices.Count(invoice => string.Equals(invoice.DocStatus, "Open", StringComparison.OrdinalIgnoreCase));
    private decimal CurrentPageTotal => Invoices.Sum(invoice => invoice.DocTotal);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || hasInitialized)
            return;

        hasInitialized = true;
        await LoadSuppliersAsync();
        await LoadInvoicesAsync();
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
            Logger.LogWarning(ex, "Failed to load supplier cache for purchase invoice filters");
        }
    }

    private async Task LoadInvoicesAsync()
    {
        isLoading = true;
        errorMessage = null;

        try
        {
            var result = await Mediator.Send(new GetPurchaseInvoicesQuery(
                currentPage,
                PageSize,
                string.IsNullOrWhiteSpace(supplierFilter) ? null : supplierFilter.Trim(),
                fromDate,
                toDate));

            result.SwitchFirst(
                value =>
                {
                    invoiceResponse = value;
                    selectedInvoice = value.Invoices.FirstOrDefault();
                },
                error =>
                {
                    invoiceResponse = new PurchaseInvoiceListResponse
                    {
                        Page = currentPage,
                        PageSize = PageSize
                    };
                    selectedInvoice = null;
                    errorMessage = error.Description;
                });

            if (!result.IsError && !hasLoggedView)
            {
                hasLoggedView = true;
                await AuditService.LogAsync(AuditActions.ViewPurchaseInvoices, "PurchaseInvoice", null);
            }
        }
        finally
        {
            isLoading = false;
        }
    }

    private void SelectInvoice(PurchaseInvoiceDto invoice)
    {
        selectedInvoice = invoice;
    }

    private async Task SearchAsync()
    {
        if (!TryResolveSupplierFilter())
            return;

        currentPage = 1;
        await LoadInvoicesAsync();
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
        await LoadInvoicesAsync();
    }

    private async Task ReloadAsync()
    {
        await LoadInvoicesAsync();
    }

    private async Task PreviousPageAsync()
    {
        if (currentPage <= 1)
            return;

        currentPage--;
        await LoadInvoicesAsync();
    }

    private async Task NextPageAsync()
    {
        if (!(invoiceResponse?.HasMore ?? false))
            return;

        currentPage++;
        await LoadInvoicesAsync();
    }

    private void NavigateToCreate()
    {
        NavigationManager.NavigateTo("/purchase-invoices/create");
    }

    private void ShowSupplierResults()
    {
        showSupplierResults = true;
    }

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
            errorMessage = "Select a supplier from the suggestions to filter invoices by name.";
            showSupplierResults = true;
            return false;
        }

        supplierFilter = searchTerm;
        selectedSupplier = null;
        errorMessage = null;
        return true;
    }

    private static string GetStatusCssClass(string? status) => status?.ToLowerInvariant() switch
    {
        "closed" => "closed",
        "cancelled" => "cancelled",
        _ => "open"
    };
}