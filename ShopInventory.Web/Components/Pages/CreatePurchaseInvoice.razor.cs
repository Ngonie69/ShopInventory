using MediatR;
using Microsoft.AspNetCore.Components;
using ShopInventory.Web.Data;
using ShopInventory.Web.Features.PurchaseInvoices.Commands.CreatePurchaseInvoice;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

public partial class CreatePurchaseInvoice
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IMasterDataCacheService CacheService { get; set; } = default!;
    [Inject] private IAuditService AuditService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ILogger<CreatePurchaseInvoice> Logger { get; set; } = default!;

    private readonly Dictionary<string, string> validationErrors = new();
    private CreatePurchaseInvoiceRequest purchaseInvoice = CreateDefaultRequest();
    private List<BusinessPartnerDto> suppliers = new();
    private List<ProductDto> products = new();
    private List<WarehouseDto> warehouses = new();
    private BusinessPartnerDto? selectedSupplier;
    private string supplierSearchTerm = string.Empty;
    private string? errorMessage;
    private string? successMessage;
    private bool isLoadingLookups = true;
    private bool isSubmitting;
    private bool showSupplierResults;
    private bool hasInitialized;

    private IEnumerable<BusinessPartnerDto> FilteredSuppliers => suppliers
        .Where(supplier =>
            !string.IsNullOrWhiteSpace(supplier.CardCode) &&
            (string.IsNullOrWhiteSpace(supplierSearchTerm) ||
             supplier.CardCode.Contains(supplierSearchTerm, StringComparison.OrdinalIgnoreCase) ||
             (supplier.CardName?.Contains(supplierSearchTerm, StringComparison.OrdinalIgnoreCase) ?? false)))
        .Take(12)
        .ToList();

    private decimal SubTotal => purchaseInvoice.Lines.Sum(CalculateLineTotal);
    private decimal HeaderDiscountAmount => SubTotal * (purchaseInvoice.DiscountPercent / 100m);
    private decimal EstimatedTotal => Math.Max(0, SubTotal - HeaderDiscountAmount);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || hasInitialized)
            return;

        hasInitialized = true;
        await LoadLookupsAsync();
        StateHasChanged();
    }

    private static CreatePurchaseInvoiceRequest CreateDefaultRequest() => new()
    {
        DocDate = DateTime.Today,
        DocDueDate = DateTime.Today,
        TaxDate = DateTime.Today,
        DocCurrency = "USD",
        Lines = new List<CreatePurchaseInvoiceLineRequest>
        {
            new()
        }
    };

    private async Task LoadLookupsAsync()
    {
        errorMessage = null;
        isLoadingLookups = true;

        try
        {
            var supplierTask = CacheService.GetBusinessPartnersAsync();
            var productTask = CacheService.GetProductsAsync();
            var warehouseTask = CacheService.GetWarehousesAsync();

            await Task.WhenAll(supplierTask, productTask, warehouseTask);

            suppliers = await supplierTask;
            products = await productTask;
            warehouses = await warehouseTask;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load purchase invoice lookups");
            errorMessage = "Failed to load suppliers, products, or warehouses. Exact codes can still be entered manually.";
        }
        finally
        {
            isLoadingLookups = false;
        }
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
        purchaseInvoice.CardCode = supplier.CardCode ?? string.Empty;
        supplierSearchTerm = supplier.DisplayName;
        showSupplierResults = false;
        validationErrors.Remove("CardCode");

        if (!string.IsNullOrWhiteSpace(supplier.Currency) && !string.Equals(supplier.Currency, "##", StringComparison.OrdinalIgnoreCase))
        {
            purchaseInvoice.DocCurrency = supplier.Currency;
        }
    }

    private void ClearSupplier()
    {
        selectedSupplier = null;
        supplierSearchTerm = string.Empty;
        purchaseInvoice.CardCode = string.Empty;
        showSupplierResults = true;
    }

    private void AddLine()
    {
        purchaseInvoice.Lines.Add(new CreatePurchaseInvoiceLineRequest());
    }

    private void RemoveLine(int index)
    {
        if (purchaseInvoice.Lines.Count == 1)
            return;

        purchaseInvoice.Lines.RemoveAt(index);
        ClearLineValidation(index);
    }

    private void SyncProduct(int index)
    {
        if (index < 0 || index >= purchaseInvoice.Lines.Count)
            return;

        var line = purchaseInvoice.Lines[index];
        if (string.IsNullOrWhiteSpace(line.ItemCode))
            return;

        var product = products.FirstOrDefault(item =>
            string.Equals(item.ItemCode, line.ItemCode, StringComparison.OrdinalIgnoreCase));

        if (product is null)
            return;

        if (string.IsNullOrWhiteSpace(line.ItemDescription))
        {
            line.ItemDescription = product.ItemName;
        }

        if (string.IsNullOrWhiteSpace(line.WarehouseCode))
        {
            line.WarehouseCode = product.DefaultWarehouse;
        }

        if (line.UnitPrice <= 0 && product.Price > 0)
        {
            line.UnitPrice = product.Price;
        }
    }

    private decimal CalculateLineTotal(CreatePurchaseInvoiceLineRequest line)
    {
        var gross = line.Quantity * line.UnitPrice;
        var lineDiscountAmount = gross * (line.DiscountPercent / 100m);
        return Math.Max(0, gross - lineDiscountAmount);
    }

    private bool HasError(string key) => validationErrors.ContainsKey(key);

    private string GetError(string key) => validationErrors.TryGetValue(key, out var value) ? value : string.Empty;

    private void ClearLineValidation(int removedIndex)
    {
        var keysToRemove = validationErrors.Keys
            .Where(key => key.StartsWith($"Lines[{removedIndex}]", StringComparison.Ordinal))
            .ToList();

        foreach (var key in keysToRemove)
        {
            validationErrors.Remove(key);
        }
    }

    private bool Validate()
    {
        validationErrors.Clear();
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(purchaseInvoice.CardCode))
        {
            validationErrors["CardCode"] = "Supplier code is required.";
        }

        if (purchaseInvoice.Lines.Count == 0)
        {
            validationErrors["Lines"] = "At least one invoice line is required.";
        }

        for (var index = 0; index < purchaseInvoice.Lines.Count; index++)
        {
            var line = purchaseInvoice.Lines[index];
            if (string.IsNullOrWhiteSpace(line.ItemCode))
                validationErrors[$"Lines[{index}].ItemCode"] = "Item code is required.";
            if (line.Quantity <= 0)
                validationErrors[$"Lines[{index}].Quantity"] = "Quantity must be greater than zero.";
            if (line.UnitPrice <= 0)
                validationErrors[$"Lines[{index}].UnitPrice"] = "Unit price must be greater than zero.";
        }

        return validationErrors.Count == 0;
    }

    private async Task SubmitAsync()
    {
        if (!Validate())
            return;

        isSubmitting = true;
        errorMessage = null;
        successMessage = null;

        try
        {
            PurchaseInvoiceDto? createdInvoice = null;
            var result = await Mediator.Send(new CreatePurchaseInvoiceCommand(purchaseInvoice));

            result.SwitchFirst(
                value => createdInvoice = value,
                error => errorMessage = error.Description);

            if (createdInvoice is null)
            {
                await AuditService.LogAsync(
                    AuditActions.CreatePurchaseInvoice,
                    "PurchaseInvoice",
                    null,
                    $"Supplier {purchaseInvoice.CardCode}",
                    false,
                    errorMessage);
                return;
            }

            successMessage = $"Purchase invoice #{createdInvoice.DocNum} created successfully.";

            await AuditService.LogAsync(
                AuditActions.CreatePurchaseInvoice,
                "PurchaseInvoice",
                createdInvoice.DocEntry.ToString(),
                $"Supplier {createdInvoice.CardCode}",
                true);

            await Task.Delay(900);
            NavigationManager.NavigateTo("/purchase-invoices");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unexpected failure while posting purchase invoice");
            errorMessage = "An unexpected error occurred while creating the purchase invoice.";

            await AuditService.LogAsync(
                AuditActions.CreatePurchaseInvoice,
                "PurchaseInvoice",
                null,
                $"Supplier {purchaseInvoice.CardCode}",
                false,
                ex.Message);
        }
        finally
        {
            isSubmitting = false;
        }
    }

    private void GoBack()
    {
        NavigationManager.NavigateTo("/purchase-invoices");
    }
}