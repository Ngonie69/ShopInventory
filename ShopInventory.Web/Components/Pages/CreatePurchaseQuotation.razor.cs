using MediatR;
using Microsoft.AspNetCore.Components;
using ShopInventory.Web.Data;
using ShopInventory.Web.Features.PurchaseQuotations.Commands.CreatePurchaseQuotation;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

public partial class CreatePurchaseQuotation
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IMasterDataCacheService CacheService { get; set; } = default!;
    [Inject] private IAuditService AuditService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ILogger<CreatePurchaseQuotation> Logger { get; set; } = default!;

    private readonly Dictionary<string, string> validationErrors = new();
    private CreatePurchaseQuotationRequest purchaseQuotation = CreateDefaultRequest();
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

    private decimal SubTotal => purchaseQuotation.Lines.Sum(CalculateLineTotal);
    private decimal HeaderDiscountAmount => SubTotal * (purchaseQuotation.DiscountPercent / 100m);
    private decimal EstimatedTotal => Math.Max(0, SubTotal - HeaderDiscountAmount);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || hasInitialized)
            return;

        hasInitialized = true;
        await LoadLookupsAsync();
        StateHasChanged();
    }

    private static CreatePurchaseQuotationRequest CreateDefaultRequest() => new()
    {
        DocDate = DateTime.Today,
        DocDueDate = DateTime.Today,
        DocCurrency = "USD",
        Lines = new List<CreatePurchaseQuotationLineRequest>
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
            Logger.LogError(ex, "Failed to load purchase quotation lookups");
            errorMessage = "Failed to load suppliers, products, or warehouses. Exact codes can still be entered manually.";
        }
        finally
        {
            isLoadingLookups = false;
        }
    }

    private void ShowSupplierResults() => showSupplierResults = true;

    private async Task HandleSupplierBlur()
    {
        await Task.Delay(150);
        showSupplierResults = false;
    }

    private void OnSupplierInput(ChangeEventArgs args)
    {
        supplierSearchTerm = args.Value?.ToString() ?? string.Empty;
        if (selectedSupplier is not null && !string.Equals(selectedSupplier.DisplayName, supplierSearchTerm, StringComparison.OrdinalIgnoreCase))
        {
            selectedSupplier = null;
            purchaseQuotation.CardCode = string.Empty;
        }
        showSupplierResults = true;
    }

    private void SelectSupplier(BusinessPartnerDto supplier)
    {
        selectedSupplier = supplier;
        purchaseQuotation.CardCode = supplier.CardCode ?? string.Empty;
        supplierSearchTerm = supplier.DisplayName;
        showSupplierResults = false;
        validationErrors.Remove("CardCode");

        if (!string.IsNullOrWhiteSpace(supplier.Currency) && !string.Equals(supplier.Currency, "##", StringComparison.OrdinalIgnoreCase))
        {
            purchaseQuotation.DocCurrency = supplier.Currency;
        }
    }

    private void ClearSupplier()
    {
        selectedSupplier = null;
        supplierSearchTerm = string.Empty;
        purchaseQuotation.CardCode = string.Empty;
        showSupplierResults = true;
    }

    private void AddLine()
    {
        purchaseQuotation.Lines.Add(new CreatePurchaseQuotationLineRequest());
    }

    private void RemoveLine(int index)
    {
        if (purchaseQuotation.Lines.Count == 1)
            return;

        purchaseQuotation.Lines.RemoveAt(index);
        ClearLineValidation(index);
    }

    private void SyncProduct(int index)
    {
        if (index < 0 || index >= purchaseQuotation.Lines.Count)
            return;

        var line = purchaseQuotation.Lines[index];
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

        if (string.IsNullOrWhiteSpace(line.UoMCode))
        {
            line.UoMCode = product.UoM;
        }
    }

    private decimal CalculateLineTotal(CreatePurchaseQuotationLineRequest line)
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

    private bool EnsureSupplierSelection()
    {
        var searchTerm = supplierSearchTerm.Trim();

        if (selectedSupplier is not null)
        {
            purchaseQuotation.CardCode = selectedSupplier.CardCode ?? string.Empty;
            return !string.IsNullOrWhiteSpace(purchaseQuotation.CardCode);
        }

        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            purchaseQuotation.CardCode = string.Empty;
            return false;
        }

        var exactCodeMatch = suppliers.FirstOrDefault(supplier => string.Equals(supplier.CardCode, searchTerm, StringComparison.OrdinalIgnoreCase));
        if (exactCodeMatch is not null)
        {
            SelectSupplier(exactCodeMatch);
            return true;
        }

        var exactNameMatch = suppliers.FirstOrDefault(supplier =>
            string.Equals(supplier.CardName, searchTerm, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(supplier.DisplayName, searchTerm, StringComparison.OrdinalIgnoreCase));
        if (exactNameMatch is not null)
        {
            SelectSupplier(exactNameMatch);
            return true;
        }

        purchaseQuotation.CardCode = searchTerm.Contains(" - ", StringComparison.Ordinal) ? searchTerm.Split(" - ", 2, StringSplitOptions.None)[0].Trim() : searchTerm;
        return !string.IsNullOrWhiteSpace(purchaseQuotation.CardCode);
    }

    private bool Validate()
    {
        validationErrors.Clear();
        errorMessage = null;

        if (!EnsureSupplierSelection())
        {
            validationErrors["CardCode"] = "Supplier code is required.";
        }

        if (purchaseQuotation.Lines.Count == 0)
        {
            validationErrors["Lines"] = "At least one quotation line is required.";
        }

        for (var index = 0; index < purchaseQuotation.Lines.Count; index++)
        {
            var line = purchaseQuotation.Lines[index];
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
            PurchaseQuotationDto? createdQuotation = null;
            var result = await Mediator.Send(new CreatePurchaseQuotationCommand(purchaseQuotation));

            result.SwitchFirst(
                value => createdQuotation = value,
                error => errorMessage = error.Description);

            if (createdQuotation is null)
            {
                await AuditService.LogAsync(
                    AuditActions.CreatePurchaseQuotation,
                    "PurchaseQuotation",
                    null,
                    $"Supplier {purchaseQuotation.CardCode}",
                    false,
                    errorMessage);
                return;
            }

            successMessage = $"Purchase quotation #{createdQuotation.DocNum} created successfully.";

            await AuditService.LogAsync(
                AuditActions.CreatePurchaseQuotation,
                "PurchaseQuotation",
                createdQuotation.DocEntry.ToString(),
                $"Supplier {createdQuotation.CardCode}",
                true);

            await Task.Delay(900);
            NavigationManager.NavigateTo("/purchase-quotations");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unexpected failure while posting purchase quotation");
            errorMessage = "An unexpected error occurred while creating the purchase quotation.";

            await AuditService.LogAsync(
                AuditActions.CreatePurchaseQuotation,
                "PurchaseQuotation",
                null,
                $"Supplier {purchaseQuotation.CardCode}",
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
        NavigationManager.NavigateTo("/purchase-quotations");
    }
}