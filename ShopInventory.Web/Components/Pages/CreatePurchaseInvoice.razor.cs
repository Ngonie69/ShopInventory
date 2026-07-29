using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
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
    private readonly Dictionary<int, LineState> lineStates = new();

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

    private sealed class LineState
    {
        public string SearchTerm { get; set; } = string.Empty;
        public bool ShowProductDropdown { get; set; }
        public List<ProductDto> FilteredProducts { get; set; } = new();
        public ProductDto? SuggestedProduct { get; set; }
        public string SuggestionSuffix { get; set; } = string.Empty;
    }

    private IEnumerable<BusinessPartnerDto> FilteredSuppliers => suppliers
        .Where(supplier =>
            !string.IsNullOrWhiteSpace(supplier.CardCode) &&
            (string.IsNullOrWhiteSpace(supplierSearchTerm) ||
             supplier.CardCode.Contains(supplierSearchTerm, StringComparison.OrdinalIgnoreCase) ||
             (supplier.CardName?.Contains(supplierSearchTerm, StringComparison.OrdinalIgnoreCase) ?? false)))
        .Take(15)
        .ToList();

    private decimal SubTotal => purchaseInvoice.Lines.Sum(CalculateLineTotal);
    private decimal HeaderDiscountAmount => SubTotal * (purchaseInvoice.DiscountPercent / 100m);
    private decimal EstimatedTotal => Math.Max(0, SubTotal - HeaderDiscountAmount);

    protected override async Task OnInitializedAsync()
    {
        await LoadLookupsAsync();
    }

    private static CreatePurchaseInvoiceRequest CreateDefaultRequest() => new()
    {
        DocDate = DateTime.UtcNow.Date,
        DocDueDate = DateTime.UtcNow.Date,
        TaxDate = DateTime.UtcNow.Date,
        DocCurrency = "USD",
        Lines = new List<CreatePurchaseInvoiceLineRequest>
        {
            new()
            {
                Quantity = 1
            }
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

            suppliers = (await supplierTask)
                .Where(partner => string.Equals(partner.CardType, "cSupplier", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(partner.CardType, "S", StringComparison.OrdinalIgnoreCase))
                .ToList();
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

    private void OnSupplierInput(ChangeEventArgs e)
    {
        supplierSearchTerm = e.Value?.ToString() ?? string.Empty;
        showSupplierResults = true;

        if (selectedSupplier != null && !string.Equals(selectedSupplier.DisplayName, supplierSearchTerm, StringComparison.Ordinal))
        {
            selectedSupplier = null;
            purchaseInvoice.CardCode = string.Empty;
        }

        validationErrors.Remove("CardCode");
    }

    private async Task HandleSupplierBlur()
    {
        await Task.Delay(150);
        showSupplierResults = false;
        ResolveSupplierFromSearchTerm();
    }

    private void ResolveSupplierFromSearchTerm()
    {
        if (selectedSupplier != null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(supplierSearchTerm))
        {
            purchaseInvoice.CardCode = string.Empty;
            return;
        }

        var exactSupplier = suppliers.FirstOrDefault(supplier =>
            string.Equals(supplier.CardCode, supplierSearchTerm.Trim(), StringComparison.OrdinalIgnoreCase));

        if (exactSupplier != null)
        {
            SelectSupplier(exactSupplier);
            return;
        }

        purchaseInvoice.CardCode = supplierSearchTerm.Trim();
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
            purchaseInvoice.DocCurrency = supplier.Currency.Trim().ToUpperInvariant();
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
        purchaseInvoice.Lines.Add(new CreatePurchaseInvoiceLineRequest
        {
            Quantity = 1
        });
        validationErrors.Remove("Lines");
    }

    private void RemoveLine(int index)
    {
        if (index < 0 || index >= purchaseInvoice.Lines.Count)
        {
            return;
        }

        purchaseInvoice.Lines.RemoveAt(index);
        lineStates.Remove(index);

        ReindexLineStates(index);
        ReindexLineValidation(index);
    }

    private void ReindexLineStates(int removedIndex)
    {
        var previousStates = new Dictionary<int, LineState>(lineStates);
        lineStates.Clear();

        for (int i = 0; i < purchaseInvoice.Lines.Count; i++)
        {
            var previousIndex = i >= removedIndex ? i + 1 : i;
            if (previousStates.TryGetValue(previousIndex, out var state))
            {
                lineStates[i] = state;
            }
        }
    }

    private void ReindexLineValidation(int removedIndex)
    {
        var updatedErrors = new Dictionary<string, string>();

        foreach (var entry in validationErrors)
        {
            if (!TryParseLineErrorKey(entry.Key, out var lineIndex, out var fieldName) || string.IsNullOrWhiteSpace(fieldName))
            {
                updatedErrors[entry.Key] = entry.Value;
                continue;
            }

            if (lineIndex == removedIndex)
            {
                continue;
            }

            var targetIndex = lineIndex > removedIndex ? lineIndex - 1 : lineIndex;
            updatedErrors[$"Lines[{targetIndex}].{fieldName}"] = entry.Value;
        }

        validationErrors.Clear();
        foreach (var entry in updatedErrors)
        {
            validationErrors[entry.Key] = entry.Value;
        }
    }

    private static bool TryParseLineErrorKey(string key, out int lineIndex, out string? fieldName)
    {
        lineIndex = -1;
        fieldName = null;

        if (!key.StartsWith("Lines[", StringComparison.Ordinal))
        {
            return false;
        }

        var closeBracketIndex = key.IndexOf(']');
        if (closeBracketIndex <= 6)
        {
            return false;
        }

        if (!int.TryParse(key.Substring(6, closeBracketIndex - 6), out lineIndex))
        {
            return false;
        }

        var separatorIndex = key.IndexOf('.', closeBracketIndex);
        if (separatorIndex < 0 || separatorIndex >= key.Length - 1)
        {
            return false;
        }

        fieldName = key[(separatorIndex + 1)..];
        return true;
    }

    private LineState GetLineState(int index)
    {
        if (!lineStates.ContainsKey(index))
        {
            lineStates[index] = new LineState();
        }

        return lineStates[index];
    }

    private void OnItemCodeInput(int index, string? value)
    {
        if (index < 0 || index >= purchaseInvoice.Lines.Count)
        {
            return;
        }

        var line = purchaseInvoice.Lines[index];
        line.ItemCode = value ?? string.Empty;

        var state = GetLineState(index);
        state.SearchTerm = value ?? string.Empty;
        state.SuggestedProduct = null;
        state.SuggestionSuffix = string.Empty;

        validationErrors.Remove($"Lines[{index}].ItemCode");

        if (string.IsNullOrWhiteSpace(value))
        {
            line.ItemDescription = string.Empty;
            state.FilteredProducts = new List<ProductDto>();
            state.ShowProductDropdown = false;
            StateHasChanged();
            return;
        }

        var searchLower = value.ToLowerInvariant();
        var searchUpper = value.ToUpperInvariant();

        state.FilteredProducts = products.Where(product =>
            (product.ItemCode?.ToUpperInvariant().StartsWith(searchUpper) ?? false) ||
            (product.ItemCode?.ToLowerInvariant().Contains(searchLower) ?? false) ||
            (product.ItemName?.ToLowerInvariant().Contains(searchLower) ?? false) ||
            (product.BarCode?.ToLowerInvariant().Contains(searchLower) ?? false))
            .OrderByDescending(product => product.ItemCode?.ToUpperInvariant().StartsWith(searchUpper) ?? false)
            .ThenBy(product => product.ItemCode)
            .ToList();

        var exactMatch = state.FilteredProducts.FirstOrDefault(product =>
            product.ItemCode?.ToUpperInvariant().StartsWith(searchUpper) ?? false);

        if (exactMatch != null && !string.IsNullOrEmpty(exactMatch.ItemCode) && exactMatch.ItemCode.Length >= value.Length)
        {
            state.SuggestedProduct = exactMatch;
            state.SuggestionSuffix = exactMatch.ItemCode.Substring(value.Length);
        }

        state.ShowProductDropdown = state.FilteredProducts.Any();
        StateHasChanged();
    }

    private async Task OnItemCodeKeyDown(int index, KeyboardEventArgs e)
    {
        var state = GetLineState(index);

        if ((e.Key == "Tab" || e.Key == "ArrowRight") && state.SuggestedProduct != null)
        {
            if (e.Key == "Tab" || !string.IsNullOrEmpty(state.SuggestionSuffix))
            {
                SelectProduct(index, state.SuggestedProduct);
                await Task.CompletedTask;
            }
        }
        else if (e.Key == "ArrowDown" && state.ShowProductDropdown && state.FilteredProducts.Any())
        {
            var currentIndex = state.SuggestedProduct != null ? state.FilteredProducts.IndexOf(state.SuggestedProduct) : -1;
            var nextIndex = Math.Min(currentIndex + 1, state.FilteredProducts.Count - 1);
            state.SuggestedProduct = state.FilteredProducts[nextIndex];
            StateHasChanged();
        }
        else if (e.Key == "ArrowUp" && state.ShowProductDropdown && state.FilteredProducts.Any())
        {
            var currentIndex = state.SuggestedProduct != null ? state.FilteredProducts.IndexOf(state.SuggestedProduct) : state.FilteredProducts.Count;
            var previousIndex = Math.Max(currentIndex - 1, 0);
            state.SuggestedProduct = state.FilteredProducts[previousIndex];
            StateHasChanged();
        }
        else if (e.Key == "Enter" && state.SuggestedProduct != null)
        {
            SelectProduct(index, state.SuggestedProduct);
            await Task.CompletedTask;
        }
        else if (e.Key == "Escape")
        {
            state.ShowProductDropdown = false;
            state.SuggestedProduct = null;
            state.SuggestionSuffix = string.Empty;
            StateHasChanged();
        }
    }

    private void OnItemCodeFocus(int index)
    {
        var state = GetLineState(index);
        if (!string.IsNullOrEmpty(state.SearchTerm) || products.Any())
        {
            state.ShowProductDropdown = true;
            if (string.IsNullOrEmpty(state.SearchTerm))
            {
                state.FilteredProducts = products.Take(15).ToList();
            }
        }
    }

    private async Task OnItemCodeBlur(int index)
    {
        await Task.Delay(200);
        TrySyncProduct(index);

        var state = GetLineState(index);
        state.ShowProductDropdown = false;
        state.SuggestionSuffix = string.Empty;
        StateHasChanged();
    }

    private void TrySyncProduct(int index)
    {
        if (index < 0 || index >= purchaseInvoice.Lines.Count)
        {
            return;
        }

        var line = purchaseInvoice.Lines[index];
        if (string.IsNullOrWhiteSpace(line.ItemCode))
        {
            return;
        }

        var product = products.FirstOrDefault(item =>
            string.Equals(item.ItemCode, line.ItemCode, StringComparison.OrdinalIgnoreCase));

        if (product is not null)
        {
            ApplyProductToLine(index, product);
        }
    }

    private void SelectProduct(int index, ProductDto product)
    {
        ApplyProductToLine(index, product);

        var state = GetLineState(index);
        state.ShowProductDropdown = false;
        state.SearchTerm = product.ItemCode ?? string.Empty;
        state.SuggestedProduct = null;
        state.SuggestionSuffix = string.Empty;

        StateHasChanged();
    }

    private void ApplyProductToLine(int index, ProductDto product)
    {
        var line = purchaseInvoice.Lines[index];
        line.ItemCode = product.ItemCode ?? string.Empty;
        line.ItemDescription = product.ItemName ?? string.Empty;

        if (string.IsNullOrWhiteSpace(line.WarehouseCode))
        {
            line.WarehouseCode = product.DefaultWarehouse;
        }

        if (line.UnitPrice <= 0 && product.Price > 0)
        {
            line.UnitPrice = product.Price;
        }

        validationErrors.Remove($"Lines[{index}].ItemCode");
        if (line.UnitPrice > 0)
        {
            validationErrors.Remove($"Lines[{index}].UnitPrice");
        }
    }

    private void OnQuantityChange(int index, string? value)
    {
        if (index < 0 || index >= purchaseInvoice.Lines.Count)
        {
            return;
        }

        var line = purchaseInvoice.Lines[index];
        if (decimal.TryParse(value, out var quantity))
        {
            line.Quantity = quantity < 0 ? 0 : quantity;
        }
        else
        {
            line.Quantity = 0;
        }

        validationErrors.Remove($"Lines[{index}].Quantity");
        StateHasChanged();
    }

    private void OnUnitPriceChange(int index, string? value)
    {
        if (index < 0 || index >= purchaseInvoice.Lines.Count)
        {
            return;
        }

        var line = purchaseInvoice.Lines[index];
        if (decimal.TryParse(value, out var unitPrice))
        {
            line.UnitPrice = unitPrice < 0 ? 0 : unitPrice;
        }
        else
        {
            line.UnitPrice = 0;
        }

        if (line.UnitPrice > 0)
        {
            validationErrors.Remove($"Lines[{index}].UnitPrice");
        }

        StateHasChanged();
    }

    private decimal CalculateLineTotal(CreatePurchaseInvoiceLineRequest line)
    {
        var gross = line.Quantity * line.UnitPrice;
        var lineDiscountAmount = gross * (line.DiscountPercent / 100m);
        return Math.Max(0, gross - lineDiscountAmount);
    }

    private bool HasError(string key) => validationErrors.ContainsKey(key);

    private string GetError(string key) => validationErrors.TryGetValue(key, out var value) ? value : string.Empty;

    private string TruncateName(string? value, int maxLength)
    {
        return value?.Length > maxLength ? value[..maxLength] + "..." : value ?? string.Empty;
    }

    private bool Validate()
    {
        validationErrors.Clear();
        errorMessage = null;

        ResolveSupplierFromSearchTerm();

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
            {
                validationErrors[$"Lines[{index}].ItemCode"] = "Item code is required.";
            }
            if (line.Quantity <= 0)
            {
                validationErrors[$"Lines[{index}].Quantity"] = "Quantity must be greater than zero.";
            }
            if (line.UnitPrice <= 0)
            {
                validationErrors[$"Lines[{index}].UnitPrice"] = "Unit price must be greater than zero.";
            }
        }

        return validationErrors.Count == 0;
    }

    private async Task SubmitAsync()
    {
        if (!Validate())
        {
            return;
        }

        purchaseInvoice.CardCode = purchaseInvoice.CardCode.Trim();
        purchaseInvoice.DocCurrency = string.IsNullOrWhiteSpace(purchaseInvoice.DocCurrency)
            ? null
            : purchaseInvoice.DocCurrency.Trim().ToUpperInvariant();

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